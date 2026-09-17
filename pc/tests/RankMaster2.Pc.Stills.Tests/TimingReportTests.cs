using System.Diagnostics;
using RankMaster2.Pc.Stills;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// Prints decode-to-frame wall time and peak bytes for the files plan section 3.4 measured,
/// at two pane sizes. This box's numbers, not the owner's (plan section 7): "Wall-clock numbers
/// on the owner's CPU and disk ... his will differ." Only loose upper bounds are asserted; the
/// printed table is what plan section 5 phase 5 hands to the integrator.
/// </summary>
public class TimingReportTests(ITestOutputHelper output)
{
    private static readonly string? Stills = Corpus.Directory("stills");

    private static readonly string[] Files =
    [
        "photo_large.jpg",
        "bomb_40mp.jpg",
        "lossless.webp",
        "interlaced.png",
        "exif_portrait_6.jpg",
    ];

    [SkippableFact]
    public void Timing_report()
    {
        Skip.If(Stills is null, "corpus not built");

        output.WriteLine($"{"file",-24} {"pane",-11} {"ms",6} {"peak MB",8}");

        double? photoLargeAt960Ms = null;
        long? bombPeakAt4k = null;

        foreach (var name in Files)
        {
            var path = Path.Combine(Stills, name);
            if (!File.Exists(path)) { output.WriteLine($"{name}: skipped, not in the corpus"); continue; }

            foreach (var (paneW, paneH, label) in new[] { (960, 1080, "960x1080"), (1920, 2160, "1920x2160") })
            {
                var budget = new DecodeBudget(512L * 1024 * 1024);
                var decoder = new StillDecoder(budget);

                var sw = Stopwatch.StartNew();
                var result = decoder.Decode(path, paneW, paneH);
                sw.Stop();

                if (!result.IsSuccess)
                {
                    output.WriteLine($"{name,-24} {label,-11} FAILED: {result.Failure} {result.Detail}");
                    continue;
                }

                var peakMb = budget.PeakBytes / 1024.0 / 1024.0;
                output.WriteLine($"{name,-24} {label,-11} {sw.Elapsed.TotalMilliseconds,6:F0} {peakMb,8:F2}");

                if (name == "photo_large.jpg" && paneW == 960) photoLargeAt960Ms = sw.Elapsed.TotalMilliseconds;
                if (name == "bomb_40mp.jpg" && paneW == 1920) bombPeakAt4k = budget.PeakBytes;

                result.Frame!.Release();
            }
        }

        if (photoLargeAt960Ms is { } ms)
            Assert.True(ms < 400, $"photo_large.jpg at 960x1080 took {ms:F0} ms, expected < 400 ms on this box.");

        if (bombPeakAt4k is { } peak)
            Assert.True(peak < 40L * 1024 * 1024, $"bomb_40mp.jpg peaked at {peak / 1024 / 1024} MB at a 4K pane, expected < 40 MB.");
    }
}
