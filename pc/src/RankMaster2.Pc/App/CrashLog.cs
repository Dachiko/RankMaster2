namespace RankMaster2.Pc.App;

/// <summary>
/// A-startup-and-shell.md § 1: "one text file next to the exe, crash-&lt;yyyyMMdd-HHmmss&gt;.txt —
/// the only debugging channel from the owner's PC." Appends part D's <c>DiagnosticsDump()</c>
/// (D § 9) when one is available.
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

    /// <summary>Writes the crash file and returns its path. Public so a test can call it directly
    /// without raising a real unhandled exception.</summary>
    public static string Write(Exception? ex, bool terminating)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Rank Master 2 crash — {DateTime.UtcNow:O} — terminating={terminating}");
        sb.AppendLine(ex?.ToString() ?? "(no exception object)");

        if (_diagnostics is not null)
        {
            sb.AppendLine();
            sb.AppendLine("-- diagnostics --");
            try { sb.AppendLine(_diagnostics()); }
            catch (Exception dumpEx) { sb.AppendLine($"(DiagnosticsDump failed: {dumpEx.Message})"); }
        }

        try { File.WriteAllText(path, sb.ToString()); }
        catch { /* nothing else to do from inside a crash handler */ }

        return path;
    }
}
