namespace RankMaster2.Pc.App;

/// <summary>
/// A-startup-and-shell.md § 1: "one text file next to the exe, crash-&lt;yyyyMMdd-HHmmss&gt;.txt —
/// the only debugging channel from the owner's PC." Appends part D's <c>DiagnosticsDump()</c>
/// (D § 9) when one is available.
///
/// G-audit-remediation.md A25: "next to the exe" is <c>AppContext.BaseDirectory</c>, which
/// <c>install.ps1</c> destroys and rebuilds on every install (step 5 renames the whole directory
/// to <c>.old</c> and deletes it) — a crash from the run that prompted the next install would
/// never survive to be read. The file now lives in the same durable, per-user directory
/// <c>StartupClock</c> already writes its log to, which no install touches, with a millisecond
/// timestamp plus a numeric suffix on collision so two crashes in one second never overwrite each
/// other.
/// </summary>
public static class CrashLog
{
    private static Func<string>? _diagnostics;
    private static bool _installed;

    public static void Install(Func<string>? diagnostics = null)
    {
        _diagnostics = diagnostics;
        if (_installed)
            return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, terminating: false);
            e.SetObserved();
        };
    }

    /// <summary>Attaches D's <c>DiagnosticsDump()</c> once <see cref="Composition.Build"/> has
    /// constructed the engine — <see cref="Install"/> runs earlier, before any part exists.</summary>
    public static void SetDiagnostics(Func<string> diagnostics) => _diagnostics = diagnostics;

    /// <summary>A25: the install directory is destroyed and recreated by every
    /// <c>install.ps1</c> run (step 5 renames it to <c>.old</c> and deletes that), so a crash file
    /// written beside the exe never survives the next install. This is the same durable directory
    /// <see cref="StartupClock.DefaultLogPath"/> already uses —
    /// <c>%LOCALAPPDATA%\RankMaster2\pc</c> on Windows, <c>$XDG_DATA_HOME/RankMaster2/pc</c> (or
    /// <c>~/.local/share/RankMaster2/pc</c>) elsewhere.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RankMaster2", "pc");

    /// <summary>Writes the crash file and returns its path. Public so a test can call it directly
    /// without raising a real unhandled exception.</summary>
    public static string Write(Exception? ex, bool terminating, string? directory = null)
    {
        directory ??= DefaultDirectory();
        var path = UniquePath(directory, DateTime.Now);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Rank Master 3 crash — {DateTime.UtcNow:O} — terminating={terminating}");
        sb.AppendLine(ex?.ToString() ?? "(no exception object)");

        if (_diagnostics is not null)
        {
            sb.AppendLine();
            sb.AppendLine("-- diagnostics --");
            try { sb.AppendLine(_diagnostics()); }
            catch (Exception dumpEx) { sb.AppendLine($"(DiagnosticsDump failed: {dumpEx.Message})"); }
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* nothing else to do from inside a crash handler */ }

        return path;
    }

    /// <summary>A25: millisecond precision alone still lets two crashes in the same request (an
    /// unhandled exception followed immediately by <see cref="AppDomain.UnhandledException"/>'s
    /// terminating one) collide; a numeric suffix guarantees each write gets its own file rather
    /// than silently overwriting the one before it.</summary>
    private static string UniquePath(string directory, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(directory, $"crash-{stamp}.txt");
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(directory, $"crash-{stamp}-{n++}.txt");
        return path;
    }
}
