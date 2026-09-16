// D-video.md § 6.4: the Windows-side proof. Takes a plugin directory and one or more paths (or a
// folder) and prints one text block for Telegram — the tool that replaces every estimate in the plan
// with a number and that tells part A whether the pruned plugin list (§ 5.1) is right.
//
// Usage:
//   rm2vidprobe <libvlc-dir|system> <file|folder> [<file2>]
//   rm2vidprobe <libvlc-dir|system> --loop N <file>
//   rm2vidprobe --plugins-compare <full-libvlc-dir> <pruned-libvlc-dir> <file|folder>
//
// <libvlc-dir> is the folder containing libvlc.dll and plugins\ (the old app's layout, part A's
// libvlc\win-x64\). "system" (or "-") means no such folder: Core.Initialize() finds the system
// libvlc, exactly as the container tests do on Linux (D-video.md § 6.2).

using System.Diagnostics;
using RankMaster2;
using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;
using RankMaster2.Pc.VidProbe;

if (args.Length < 2)
{
    PrintUsage();
    return 1;
}

if (args[0] == "--plugins-compare")
{
    if (args.Length < 4)
    {
        PrintUsage();
        return 1;
    }

    return RunPluginsCompare(args[1], args[2], args[3..]);
}

var nativeDir = ResolveNativeDir(args[0]);
var rest = args[1..];

if (rest[0] == "--loop")
{
    if (rest.Length < 3 || !int.TryParse(rest[1], out var n))
    {
        PrintUsage();
        return 1;
    }

    return RunWithBackend(nativeDir, backend => Console.WriteLine(RunLoop(backend, rest[2], n, new VideoOptions())));
}

var targets = ResolveFiles(rest);
if (targets.Count == 0)
{
    Console.WriteLine("No recognised video files found.");
    return 1;
}

return RunWithBackend(nativeDir, backend =>
{
    var options = new VideoOptions();
    Console.WriteLine(HeaderLine(backend, nativeDir));

    foreach (var file in targets)
        Console.WriteLine(ProbeOne(backend, file, options, TimeSpan.FromSeconds(6)));

    if (targets.Count == 2)
        Console.WriteLine(ProbePair(backend, targets[0], targets[1], options, TimeSpan.FromSeconds(6)));
});

// ---------------------------------------------------------------------------

static void PrintUsage()
{
    Console.WriteLine("rm2vidprobe <libvlc-dir|system> <file|folder> [<file2>]");
    Console.WriteLine("rm2vidprobe <libvlc-dir|system> --loop N <file>");
    Console.WriteLine("rm2vidprobe --plugins-compare <full-libvlc-dir> <pruned-libvlc-dir> <file|folder>");
}

static string? ResolveNativeDir(string arg) => arg is "system" or "-" ? null : arg;

static List<string> ResolveFiles(IReadOnlyList<string> args)
{
    var files = new List<string>();
    foreach (var arg in args)
    {
        if (Directory.Exists(arg))
        {
            files.AddRange(Directory.EnumerateFiles(arg)
                .Where(f => MediaExtensions.KindOf(Path.GetFileName(f)) == MediaKind.Video)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        else if (File.Exists(arg))
        {
            files.Add(arg);
        }
    }

    return files;
}

static int RunWithBackend(string? nativeDir, Action<LibVlcBackend> body)
{
    var backend = new LibVlcBackend();
    var result = backend.Initialize(nativeDir, new VideoOptions(), _ => { });
    if (!result.Success)
    {
        Console.WriteLine($"libvlc failed to initialise: {result.FailureMessage}");
        return 3;
    }

    try
    {
        body(backend);
        return 0;
    }
    finally
    {
        backend.Dispose();
    }
}

static string HeaderLine(LibVlcBackend backend, string? nativeDir)
{
    var timing = backend.InitTiming;
    var pluginsDir = nativeDir is null ? "(system)" : Path.Combine(nativeDir, "plugins");
    var datPath = nativeDir is null ? null : Path.Combine(pluginsDir, "plugins.dat");
    var dat = datPath is not null && File.Exists(datPath) ? "present" : "absent";
    return $"libvlc {backend.Version}  plugins: {pluginsDir}  plugins.dat: {dat}  " +
           $"Core.Initialize {timing?.CoreInitialize.TotalMilliseconds:0}ms  new LibVLC {timing?.NewLibVlc.TotalMilliseconds:0}ms";
}

static string ProbeOne(IPlayerBackend backend, string path, VideoOptions options, TimeSpan sampleDuration)
{
    var callbacks = new ProbeCallbacks();
    var player = backend.CreatePlayer(callbacks);
    try
    {
        var sw = Stopwatch.StartNew();
        var parse = player.Parse(path, options.ParseTimeout);
        var parseMs = sw.ElapsedMilliseconds;

        if (parse.Outcome != ParseOutcome.Ok)
            return $"{path}\tFAILED Unreadable after {parseMs}ms (parse)";
        if (!parse.HasVideoTrack)
            return $"{path}\tFAILED NoVideoTrack after {parseMs}ms (parse)";

        const int gen = 1;
        callbacks.Reset(gen);
        sw.Restart();
        if (!player.Play(options, gen))
            return $"{path}\tFAILED DecodeFailed: Play() returned false";

        var gotOutcome = callbacks.WaitFirstOutcome(options.FirstFrameTimeout);
        var firstMs = sw.ElapsedMilliseconds;

        if (!gotOutcome)
            return $"{path}\tFAILED NoFrameInTime after {firstMs}ms";
        if (callbacks.EndedBeforeFirstFrame)
            return $"{path}\tFAILED DecodeFailed: the file ends before its first frame ({firstMs}ms)";
        if (callbacks.Errored)
            return $"{path}\tFAILED DecodeFailed after {firstMs}ms";

        Thread.Sleep(sampleDuration);

        var stats = player.Statistics();
        var total = stats.DecodedFrames + stats.LostFrames;
        var rate = total > 0 ? 100.0 * stats.LostFrames / total : 0.0;
        var ws = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

        return $"{path}\tparse {parseMs}ms  first frame {firstMs}ms  {parse.Width}x{parse.Height} {parse.Codec}  " +
               $"{sampleDuration.TotalSeconds:0.0}s: decoded {stats.DecodedFrames} lost {stats.LostFrames} ({rate:0.0}%)  ws {ws}MB";
    }
    catch (Exception ex)
    {
        return $"{path}\tFAILED exception: {ex.Message}";
    }
    finally
    {
        player.Stop();
        player.Dispose();
    }
}

static string ProbePair(IPlayerBackend backend, string pathA, string pathB, VideoOptions options, TimeSpan sampleDuration)
{
    var cbA = new ProbeCallbacks();
    var cbB = new ProbeCallbacks();
    var playerA = backend.CreatePlayer(cbA);
    var playerB = backend.CreatePlayer(cbB);
    try
    {
        var parseA = playerA.Parse(pathA, options.ParseTimeout);
        var parseB = playerB.Parse(pathB, options.ParseTimeout);
        if (parseA.Outcome != ParseOutcome.Ok || !parseA.HasVideoTrack)
            return $"PAIR {pathA} + {pathB}\tFAILED: {pathA} did not parse to a playable video track";
        if (parseB.Outcome != ParseOutcome.Ok || !parseB.HasVideoTrack)
            return $"PAIR {pathA} + {pathB}\tFAILED: {pathB} did not parse to a playable video track";

        const int gen = 1;
        cbA.Reset(gen);
        cbB.Reset(gen);
        if (!playerA.Play(options, gen) || !playerB.Play(options, gen))
            return $"PAIR {pathA} + {pathB}\tFAILED: Play() returned false";

        var gotA = cbA.WaitFirstOutcome(options.FirstFrameTimeout);
        var gotB = cbB.WaitFirstOutcome(options.FirstFrameTimeout);
        if (!gotA || !gotB || cbA.Errored || cbB.Errored)
            return $"PAIR {pathA} + {pathB}\tFAILED to reach the first frame on both sides";

        Thread.Sleep(sampleDuration);

        var sA = playerA.Statistics();
        var sB = playerB.Statistics();
        var ws = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

        return $"PAIR {pathA} + {pathB}\t{sampleDuration.TotalSeconds:0.0}s: left lost {RateOf(sA):0.0}%  " +
               $"right lost {RateOf(sB):0.0}%   process ws {ws}MB";
    }
    finally
    {
        playerA.Stop();
        playerB.Stop();
        playerA.Dispose();
        playerB.Dispose();
    }

    static double RateOf(BackendStatistics s)
    {
        var total = s.DecodedFrames + s.LostFrames;
        return total > 0 ? 100.0 * s.LostFrames / total : 0.0;
    }
}

static string RunLoop(IPlayerBackend backend, string path, int n, VideoOptions options)
{
    GC.Collect();
    var wsBefore = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

    for (var i = 0; i < n; i++)
    {
        var callbacks = new ProbeCallbacks();
        var player = backend.CreatePlayer(callbacks);
        callbacks.Reset(1);
        var parse = player.Parse(path, options.ParseTimeout);
        if (parse.Outcome == ParseOutcome.Ok && parse.HasVideoTrack && player.Play(options, 1))
            callbacks.WaitFirstOutcome(options.FirstFrameTimeout);
        player.Stop();
        player.Dispose();
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var wsAfter = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

    return $"--loop {n} {path}\tws before {wsBefore}MB  after {wsAfter}MB  delta {wsAfter - wsBefore}MB";
}

static int RunPluginsCompare(string fullDir, string prunedDir, string[] targetArgs)
{
    var files = ResolveFiles(targetArgs);
    if (files.Count == 0)
    {
        Console.WriteLine("No recognised video files found.");
        return 1;
    }

    Console.WriteLine($"--plugins-compare {fullDir} (full) vs {prunedDir} (pruned) -- {files.Count} file(s)");

    var full = ProbeAllQuick(fullDir, files);
    var pruned = ProbeAllQuick(prunedDir, files);

    var regressions = 0;
    foreach (var file in files)
    {
        var f = full[file];
        var p = pruned[file];
        if (f && !p)
            regressions++;
        var mark = f == p ? "ok  " : "DIFF";
        Console.WriteLine($"  {mark}  {file}\tfull={(f ? "plays" : "fails")}  pruned={(p ? "plays" : "fails")}");
    }

    Console.WriteLine(regressions == 0
        ? "The pruned plugin set plays everything the full set plays: zero regressions."
        : $"{regressions} file(s) play under the full set but fail under the pruned set — see the add-back list in D-video.md § 5.1.");

    return regressions == 0 ? 0 : 2;

    static Dictionary<string, bool> ProbeAllQuick(string nativeDir, List<string> files)
    {
        var results = new Dictionary<string, bool>();
        var backend = new LibVlcBackend();
        var options = new VideoOptions();
        var initResult = backend.Initialize(nativeDir, options, _ => { });
        if (!initResult.Success)
        {
            foreach (var file in files)
                results[file] = false;
            return results;
        }

        try
        {
            foreach (var file in files)
            {
                var callbacks = new ProbeCallbacks();
                var player = backend.CreatePlayer(callbacks);
                try
                {
                    var parse = player.Parse(file, options.ParseTimeout);
                    if (parse.Outcome != ParseOutcome.Ok || !parse.HasVideoTrack)
                    {
                        results[file] = false;
                        continue;
                    }

                    callbacks.Reset(1);
                    var played = player.Play(options, 1) && callbacks.WaitFirstOutcome(options.FirstFrameTimeout)
                                 && !callbacks.Errored && !callbacks.EndedBeforeFirstFrame;
                    results[file] = played;
                }
                finally
                {
                    player.Stop();
                    player.Dispose();
                }
            }
        }
        finally
        {
            backend.Dispose();
        }

        return results;
    }
}
