using System.Diagnostics;
using RankMaster2.Pc.Stills;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>StillSourceTests's scenarios, against the real decoder and the real corpus stills/ folder as "the library".</summary>
public class StillSourceCorpusTests(ITestOutputHelper output)
{
    private static readonly string? Stills = Corpus.Directory("stills");
    private static readonly string? Broken = Corpus.Directory("broken");

    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(5);
        }
        return predicate();
    }

    private static bool DisposeIfReady(StillState state)
    {
        if (state is StillState.Ready r) { r.Lease.Dispose(); return true; }
        return false;
    }

    [SkippableFact]
    public async Task A_real_pair_becomes_Ready_within_a_second()
    {
        Skip.If(Stills is null, "corpus not built");
        var left = "photo_large.jpg";
        var right = "exif_portrait_6.jpg";
        Skip.If(!File.Exists(Path.Combine(Stills!, left)) || !File.Exists(Path.Combine(Stills!, right)), "corpus files missing");

        await using var source = new StillSource(new DecodeBudget(256L * 1024 * 1024));
        source.SetPaneSize(960, 1080);

        var sw = Stopwatch.StartNew();
        source.Show(Stills, left, right);
        Assert.True(await WaitUntil(() => source.StateOf(left) is StillState.Ready, TimeSpan.FromSeconds(1)));
        Assert.True(await WaitUntil(() => source.StateOf(right) is StillState.Ready, TimeSpan.FromSeconds(1)));
        sw.Stop();

        output.WriteLine($"both Ready after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"expected both Ready within 1000 ms, took {sw.ElapsedMilliseconds} ms");

        var leftState = (StillState.Ready)source.StateOf(left);
        var rightState = (StillState.Ready)source.StateOf(right);
        try
        {
            Assert.Equal((960, 645), (leftState.Lease.Frame.Width, leftState.Lease.Frame.Height));
            Assert.Equal((720, 1080), (rightState.Lease.Frame.Width, rightState.Lease.Frame.Height));
        }
        finally
        {
            leftState.Lease.Dispose();
            rightState.Lease.Dispose();
        }
    }

    [SkippableFact]
    public async Task Warm_pairs_make_the_next_Show_instant()
    {
        Skip.If(Stills is null, "corpus not built");
        string[] names = ["photo_cat.jpg", "photo_large.jpg", "exif_landscape_6.jpg", "lossless.webp"];
        Skip.If(names.Any(n => !File.Exists(Path.Combine(Stills!, n))), "corpus files missing");

        await using var source = new StillSource(new DecodeBudget(256L * 1024 * 1024));
        source.SetPaneSize(960, 1080);

        source.Show(Stills, "photo_cat.jpg", "photo_large.jpg");
        source.Warm(Stills, [("exif_landscape_6.jpg", "lossless.webp")]);

        Assert.True(await WaitUntil(
            () => source.StateOf("exif_landscape_6.jpg") is StillState.Ready && source.StateOf("lossless.webp") is StillState.Ready,
            TimeSpan.FromSeconds(3)));

        DisposeIfReady(source.StateOf("photo_cat.jpg"));
        DisposeIfReady(source.StateOf("photo_large.jpg"));

        var sw = Stopwatch.StartNew();
        source.Show(Stills, "exif_landscape_6.jpg", "lossless.webp");
        sw.Stop();

        var left = source.StateOf("exif_landscape_6.jpg");
        var right = source.StateOf("lossless.webp");
        try
        {
            Assert.IsType<StillState.Ready>(left);
            Assert.IsType<StillState.Ready>(right);
        }
        finally
        {
            DisposeIfReady(left);
            DisposeIfReady(right);
        }

        output.WriteLine($"warmed Show() call took {sw.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(sw.Elapsed.TotalMilliseconds < 5, $"expected the warmed Show() call to take < 5 ms, took {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    [SkippableFact]
    public async Task A_broken_file_in_a_pair_does_not_stop_the_other()
    {
        Skip.If(Stills is null || Broken is null, "corpus not built");
        var noisePath = Path.Combine(Broken!, "noise.jpg");
        var catPath = Path.Combine(Stills!, "photo_cat.jpg");
        Skip.If(!File.Exists(noisePath) || !File.Exists(catPath), "corpus files missing");

        var tempDir = Directory.CreateTempSubdirectory("rm2-stills-source-broken-");
        try
        {
            File.Copy(noisePath, Path.Combine(tempDir.FullName, "noise.jpg"));
            File.Copy(catPath, Path.Combine(tempDir.FullName, "photo_cat.jpg"));

            await using var source = new StillSource(new DecodeBudget(256L * 1024 * 1024));
            source.Show(tempDir.FullName, "noise.jpg", "photo_cat.jpg");

            Assert.True(await WaitUntil(() => source.StateOf("noise.jpg") is StillState.Failed, TimeSpan.FromSeconds(2)));
            Assert.True(await WaitUntil(() => source.StateOf("photo_cat.jpg") is StillState.Ready, TimeSpan.FromSeconds(2)));

            var failed = (StillState.Failed)source.StateOf("noise.jpg");
            Assert.Equal(StillFailure.NotAnImage, failed.Reason);
            output.WriteLine($"noise.jpg: {failed.Reason}, \"{failed.Detail}\"");

            DisposeIfReady(source.StateOf("photo_cat.jpg"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_vanished_file_is_Missing_and_Release_still_succeeds()
    {
        Skip.If(Stills is null, "corpus not built");
        var catPath = Path.Combine(Stills!, "photo_cat.jpg");
        var largePath = Path.Combine(Stills!, "photo_large.jpg");
        Skip.If(!File.Exists(catPath) || !File.Exists(largePath), "corpus files missing");

        var tempDir = Directory.CreateTempSubdirectory("rm2-stills-source-vanish-");
        try
        {
            var copyPath = Path.Combine(tempDir.FullName, "photo_cat.jpg");
            File.Copy(catPath, copyPath);
            File.Copy(largePath, Path.Combine(tempDir.FullName, "photo_large.jpg"));

            await using var source = new StillSource(new DecodeBudget(256L * 1024 * 1024));
            source.Show(tempDir.FullName, "photo_cat.jpg", "photo_large.jpg");
            Assert.True(await WaitUntil(() => source.StateOf("photo_cat.jpg") is StillState.Ready, TimeSpan.FromSeconds(2)));
            DisposeIfReady(source.StateOf("photo_cat.jpg"));
            DisposeIfReady(source.StateOf("photo_large.jpg"));

            File.Delete(copyPath);

            source.Show(tempDir.FullName, "photo_cat.jpg", "photo_large.jpg");
            var state = source.StateOf("photo_cat.jpg");
            Assert.IsType<StillState.Failed>(state);
            Assert.Equal(StillFailure.Missing, ((StillState.Failed)state).Reason);
            DisposeIfReady(source.StateOf("photo_large.jpg"));

            var released = await source.ReleaseAsync(tempDir.FullName, "photo_cat.jpg", CancellationToken.None);
            Assert.True(released);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
