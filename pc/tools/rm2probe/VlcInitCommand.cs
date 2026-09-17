namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Diagnostics;
using LibVLCSharp.Shared;

/// <summary>
/// A-startup-and-shell.md § 6.4. <c>vlc-init &lt;libvlcDir&gt; [--runs N]</c> spawns
/// <c>rm2probe vlc-init &lt;dir&gt; --once</c> per run (libvlccore's module bank is process-global —
/// § 6.1: a second <c>libvlc_new</c> in the same process does not scan again — so each sample needs
/// a fresh process). <c>--once</c> times <c>Core.Initialize</c> and <c>new LibVLC</c> separately.
/// </summary>
internal static class VlcInitCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("vlc-init: needs <libvlcDir>");
            return 1;
        }

        var dir = args[0];

        if (Program.HasFlag(args, "--once"))
            return RunOnce(dir);

        var runs = int.TryParse(Program.OptionValue(args, "--runs"), out var n) ? n : 3;
        var exe = Environment.ProcessPath ?? "rm2probe";
        var failed = 0;

        for (var i = 0; i < runs; i++)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
            };
            psi.ArgumentList.Add("vlc-init");
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("--once");

            using var p = Process.Start(psi);
            p!.WaitForExit();
            if (p.ExitCode != 0)
                failed++;
        }

        return failed == runs ? 1 : 0;
    }

    private static int RunOnce(string dir)
    {
        var sawCacheHit = false;
        var pluginCount = -1;

        void OnLog(object? sender, LogEventArgs e)
        {
            var line = e.FormattedLog ?? $"[{e.Level}] {e.Module}: {e.Message}";
            if (line.Contains("loading plugins cache file", StringComparison.OrdinalIgnoreCase))
                sawCacheHit = true;

            var idx = line.IndexOf("plug-ins loaded:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rest = line[(idx + "plug-ins loaded:".Length)..].TrimStart();
                var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var count))
                    pluginCount = count;
            }
        }

        // Core.Initialize(string) is Windows/macOS-only (see VlcCacheCommand.cs's comment); on Linux
        // the system's own libvlc is found without an argument.
        var swInit = Stopwatch.StartNew();
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                Core.Initialize(dir);
            else
                Core.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vlc-init: Core.Initialize failed: {ex.Message}");
            return 1;
        }
        swInit.Stop();

        var swNew = Stopwatch.StartNew();
        LibVLC? lib = null;
        try
        {
            lib = new LibVLC("--quiet");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vlc-init: new LibVLC failed: {ex.Message}");
            return 1;
        }
        swNew.Stop();

        // Registered after construction: LibVLCSharp's managed Log event can only observe messages
        // emitted after libvlc_log_set is wired, so lines logged synchronously inside libvlc_new
        // itself (module bank load, cache read) are not necessarily seen here. plugins=/cache= are
        // therefore best-effort, not a hard proof — the init=/new= timings are the real signal.
        lib.Log += OnLog;

        var cache = sawCacheHit ? "hit" : "none";
        Console.WriteLine($"init={swInit.ElapsedMilliseconds} new={swNew.ElapsedMilliseconds} plugins={pluginCount} cache={cache}");

        lib.Dispose();
        return 0;
    }
}
