// AUDIT2.md § 2.1, end to end on this side: a real StillSource, its two real worker threads, the
// real Skia decode, and a DecodeBudget too small to hold both pictures of a pair at once. The whole
// interaction of this product is showing a PAIR, so these two decodes are not a rare race -- they
// are every pair, every time. Before DecodeRoom, whichever asked second was refused, and the owner
// was told a photograph that is perfectly good could not be decoded.
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Stills.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

public class ConcurrentPairDecodeTests(ITestOutputHelper output) : IDisposable
{
    // 2400 x 2400: BMP has no sampled decode (nor has PNG, which is the format in the finding), so
    // each decode's buffer is the full source -- 2400 * 2400 * 4 = 23.0 MB -- plus a 960 x 960 frame
    // at 3.7 MB. One decode peaks at 26.7 MB; two at once need 53.4 MB.
    private const int Source = 2400;
    private const long Ceiling = 40L * 1024 * 1024;

    private readonly string _folder = NewFolder();

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Both_pictures_of_a_pair_are_shown_when_only_one_of_them_fits_at_a_time()
    {
        SquareBmp.WriteTo(_folder, "left.bmp", Source);
        SquareBmp.WriteTo(_folder, "right.bmp", Source);

        var budget = new DecodeBudget(Ceiling);
        var room = new DecodeRoom(budget);
        await using var source = new StillSource(new StillDecoder(budget, room));
        source.SetPaneSize(960, 1080);

        var serialised = false;
        for (var attempt = 1; attempt <= 10 && !serialised; attempt++)
        {
            source.Show(_folder, "left.bmp", "right.bmp");

            Assert.True(await WaitUntil(() => Settled(source, "left.bmp") && Settled(source, "right.bmp"), TimeSpan.FromSeconds(30)),
                "the pair never finished decoding");

            AssertShown(source, "left.bmp");
            AssertShown(source, "right.bmp");

            // Every byte stayed inside the ceiling -- the reservation did not merely paper over an
            // overrun.
            Assert.True(budget.PeakBytes <= Ceiling, $"peak {budget.PeakBytes} exceeded the {Ceiling} ceiling");

            if (room.WaitedAdmissions > 0)
            {
                serialised = true;
                output.WriteLine($"attempt {attempt}: a decode waited {room.LastWait.TotalMilliseconds:F0} ms for room and then succeeded");
            }

            await source.ReleaseAllAsync(CancellationToken.None);
        }

        // If the two decodes never actually overlapped, this test proved nothing about the finding.
        Assert.True(serialised, "the two decodes never collided, so nothing here exercised the budget");
    }

    private static void AssertShown(StillSource source, string id)
    {
        var state = source.StateOf(id);
        if (state is StillState.Failed failed)
            Assert.Fail($"{id} was refused as {failed.Reason}: {failed.Detail}");

        var ready = Assert.IsType<StillState.Ready>(state);
        Assert.Equal(960, ready.Lease.Frame.Width);
        ready.Lease.Dispose();
    }

    private static bool Settled(StillSource source, string id) =>
        source.StateOf(id) is StillState.Ready or StillState.Failed;

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

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm2-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
