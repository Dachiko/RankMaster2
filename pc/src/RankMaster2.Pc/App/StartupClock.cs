namespace RankMaster2.Pc.App;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// A-startup-and-shell.md § 6.5. The in-app clock: <see cref="Start"/> at <c>Main</c> entry,
/// <see cref="Mark"/> from any part (never throws, never blocks — a lock-free array of
/// <see cref="MaxMarks"/> slots; a 65th mark is dropped). One line per launch, appended to
/// <c>%LOCALAPPDATA%\RankMaster2\pc\startup.log</c>, capped at the last 200 lines.
///
/// Marks each part owns (§ 6.5): A — main, avalonia_built, window_opened, first_frame,
/// open_requested, probe_video/probe_still, vlc_wake_begin, vlc_wake_end, vlc_index_rebuilt,
/// snapshot, quit; B — link_connecting, link_ready, tray_started; E — panes_painted; D —
/// first_video_frame.
/// </summary>
public static class StartupClock
{
    public const int MaxMarks = 64;

    private static readonly (string Name, long Ms)[] Marks = new (string, long)[MaxMarks];
    private static int _count;
    private static Stopwatch? _stopwatch;
    private static DateTime _startWallUtc;
    private static string _version = "0.0.0";
    private static bool _vlcLoadedEarly;

    /// <summary>Call once, at the top of <c>Main</c>. Safe to call again (a test harness relaunching
    /// the clock in-process) — it simply resets.</summary>
    public static void Start(string version)
    {
        _version = version;
        _startWallUtc = DateTime.UtcNow;
        _count = 0;
        _vlcLoadedEarly = false;
        _stopwatch = Stopwatch.StartNew();
        Mark("main");
    }

    /// <summary>Appends (name, elapsed ms since <see cref="Start"/>) to the ring. Never throws;
    /// a call before <see cref="Start"/> or past <see cref="MaxMarks"/> is silently dropped.</summary>
    public static void Mark(string name)
    {
        var sw = _stopwatch;
        if (sw is null)
            return;

        var ms = sw.ElapsedMilliseconds;
        var i = System.Threading.Interlocked.Increment(ref _count) - 1;
        if (i < 0 || i >= MaxMarks)
            return;
        Marks[i] = (name, ms);
    }

    /// <summary>§ 3.1: "LibVLCSharp assembly must not be loaded" at first_frame. Appends
    /// <c>vlc_loaded_early=1</c> to the written line if it has been, and throws in Debug builds
    /// so the defect is caught here rather than shipped.</summary>
    public static void AssertNoVideoEngine()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => (a.GetName().Name ?? "").StartsWith("LibVLCSharp", StringComparison.Ordinal));
        if (!loaded)
            return;

        _vlcLoadedEarly = true;
#if DEBUG
        throw new InvalidOperationException(
            "StartupClock.AssertNoVideoEngine: LibVLCSharp was loaded before first_frame.");
#endif
    }

    /// <summary>The default log path, <c>%LOCALAPPDATA%\RankMaster2\pc\startup.log</c>. On a
    /// non-Windows box (tests, this build machine) <c>LocalApplicationData</c> still resolves to
    /// something writable under <c>$HOME</c>, which is all the tests need.</summary>
    public static string DefaultLogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RankMaster2", "pc", "startup.log");

    /// <summary>Builds the current line: <c>&lt;ISO8601Z&gt; &lt;version&gt; name=ms name=ms ...</c>,
    /// <c>pre_main</c> first when a process start time is available (§ 6.5: read only after
    /// first_frame, never before, so this method is never called earlier).</summary>
    public static string BuildLine()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_startWallUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(_version);

        var preMain = TryComputePreMain();
        if (preMain is not null)
            sb.Append(" pre_main=").Append(preMain.Value);

        var n = Math.Min(_count, MaxMarks);
        for (var i = 0; i < n; i++)
            sb.Append(' ').Append(Marks[i].Name).Append('=').Append(Marks[i].Ms);

        if (_vlcLoadedEarly)
            sb.Append(" vlc_loaded_early=1");

        return sb.ToString();
    }

    private static long? TryComputePreMain()
    {
        try
        {
            var start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var ms = (long)(_startWallUtc - start).TotalMilliseconds;
            return ms < 0 ? 0 : ms;
        }
        catch
        {
            return null; // not available on every platform/sandbox; the line is still written without it
        }
    }

    /// <summary>Writes (or rewrites) this launch's line at <paramref name="path"/> (default: § 6.5's
    /// path), keeping only the last 200 lines. Called once after first_frame and again at quit so
    /// later marks (panes_painted, first_video_frame, quit) land in the same line.</summary>
    public static void Flush(string? path = null)
    {
        path ??= DefaultLogPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        List<string> lines;
        try
        {
            lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        }
        catch
        {
            lines = new List<string>();
        }

        var line = BuildLine();
        if (lines.Count > 0 && LinesShareLaunch(lines[^1], line))
            lines[^1] = line; // rewrite this launch's line with the marks recorded since the first write
        else
            lines.Add(line);

        if (lines.Count > 200)
            lines = lines.Skip(lines.Count - 200).ToList();

        try
        {
            File.WriteAllLines(path, lines);
        }
        catch
        {
            // The log is a diagnostic aid, not load-bearing; a locked or unwritable file never
            // takes the app down.
        }
    }

    private static bool LinesShareLaunch(string previous, string current)
    {
        var pTimestamp = previous.Split(' ', 2).FirstOrDefault();
        var cTimestamp = current.Split(' ', 2).FirstOrDefault();
        return pTimestamp is not null && pTimestamp == cTimestamp;
    }

    /// <summary>Parses one line back into its parts, for the round-trip test and for the install
    /// script's summary. Never throws on a malformed line — returns null.</summary>
    public static StartupLogLine? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;
        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp))
            return null;

        var version = parts[1];
        var marks = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0)
                continue;
            var name = parts[i][..eq];
            if (long.TryParse(parts[i][(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                marks[name] = ms;
        }

        return new StartupLogLine(stamp, version, marks);
    }

    /// <summary>Test-only: resets the clock so successive tests in one process don't see each
    /// other's marks. Not called from production code.</summary>
    internal static void ResetForTests()
    {
        _stopwatch = null;
        _count = 0;
        _vlcLoadedEarly = false;
        _version = "0.0.0";
    }
}

/// <summary>One parsed line of <c>startup.log</c>.</summary>
public sealed record StartupLogLine(DateTime TimestampUtc, string Version, IReadOnlyDictionary<string, long> Marks);
