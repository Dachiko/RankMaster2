namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Diagnostics;
using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;

/// <summary>
/// A-startup-and-shell.md § 5.3, § 6.4: the harness's player. Plays every video file at
/// <paramref name="target"/> (or the one file given) through <see cref="LibVlcBackend"/> with the
/// manifest's plugin set, reporting time-to-first-frame and frame count so
/// <c>prune-check.sh</c> can assert every corpus file plays.
///
/// KNOWN LIMITATION (recorded here rather than silently shipped): <see cref="LibVlcBackend"/>'s
/// log sink — by design, for the crash file (D-video.md § 4.5) — forwards only Warning/Error level
/// lines. VLC's own "using &lt;capability&gt; module &lt;name&gt;" notices are Info-level, so the
/// per-file module list this command prints is Warning/Error lines only, not the full
/// module-selection trace § 5.3 describes. The plugin manifest itself (plugins.keep.txt) was
/// verified a different way instead — directly against the restored VideoLAN.LibVLC.Windows
/// package's file list (App/'s ManifestTests.Manifest_MatchesPackage) and against D-video.md § 5.1's
/// own per-module argument, merged in A § 4.2's table — so this gap does not leave the manifest
/// unverified, only this one harness step weaker than planned. Widening
/// <see cref="LibVlcBackend"/> to expose its raw <c>LibVLC.Log</c> (all levels) to a second
/// subscriber would close it; flagged to the coordinator rather than done here, since
/// <c>Video/Backend/LibVlcBackend.cs</c> is part D's file.
/// </summary>
internal static class PlayCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("play: needs <file-or-dir>");
            return 1;
        }

        var target = args[0];
        var libvlcDir = Program.OptionValue(args, "--libvlc");
        var seconds = int.TryParse(Program.OptionValue(args, "--seconds"), out var s) ? s : 3;
        var minFrames = int.TryParse(Program.OptionValue(args, "--min-frames"), out var m) ? m : 10;
        var reportPath = Program.OptionValue(args, "--report");

        List<string> files;
        if (Directory.Exists(target))
        {
            files = Directory.EnumerateFiles(target)
                .Where(f => RankMaster2.MediaExtensions.KindOf(Path.GetFileName(f)) == RankMaster2.MediaKind.Video)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(target))
        {
            files = new List<string> { target };
        }
        else
        {
            Console.WriteLine($"play: skipped: '{target}' does not exist");
            return 0;
        }

        if (files.Count == 0)
        {
            Console.WriteLine("play: skipped: no video files found");
            return 0;
        }

        var moduleLines = new SortedSet<string>(StringComparer.Ordinal);
        var backend = new LibVlcBackend();
        var options = new VideoOptions();
        var initResult = backend.Initialize(libvlcDir, options, line =>
        {
            if (line.Contains("module", StringComparison.OrdinalIgnoreCase))
                moduleLines.Add(line.Trim());
        });

        if (!initResult.Success)
        {
            Console.Error.WriteLine($"play: engine failed to initialise: {initResult.FailureMessage}");
            return 1;
        }

        var reportLines = new List<string>();
        var failures = 0;
        var generation = 0;

        foreach (var file in files)
        {
            generation++;
            var callbacks = new ProbeCallbacks();
            callbacks.Reset(generation);
            var player = backend.CreatePlayer(callbacks);

            var sw = Stopwatch.StartNew();
            var parse = player.Parse(file, TimeSpan.FromSeconds(10));
            if (parse.Outcome != ParseOutcome.Ok || !parse.HasVideoTrack)
            {
                var line = $"{Path.GetFileName(file)}: parse failed ({parse.Outcome}) FAIL";
                Console.WriteLine(line);
                reportLines.Add(line);
                failures++;
                player.Dispose();
                continue;
            }

            player.Play(options, generation);
            var gotFirst = callbacks.WaitFirstFrame(TimeSpan.FromSeconds(10));
            var firstMs = sw.ElapsedMilliseconds;

            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            var frames = callbacks.DisplayCount;

            player.Stop();
            player.Dispose();

            var ok = gotFirst && frames >= minFrames;
            if (!ok)
                failures++;

            var reportLine = $"{Path.GetFileName(file)}: first_frame={(gotFirst ? firstMs + "ms" : "none")} frames={frames} {(ok ? "OK" : "FAIL")}";
            Console.WriteLine(reportLine);
            reportLines.Add(reportLine);
        }

        reportLines.Add("-- log lines at warning level or above, deduplicated --");
        foreach (var line in moduleLines)
        {
            Console.WriteLine(line);
            reportLines.Add(line);
        }

        if (reportPath is not null)
        {
            var dir = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllLines(reportPath, reportLines);
        }

        backend.Dispose();
        return failures > 0 ? 1 : 0;
    }
}
