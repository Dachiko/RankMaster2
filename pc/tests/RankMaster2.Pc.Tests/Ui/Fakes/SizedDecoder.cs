using RankMaster2.Pc.Stills;

namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>
/// The <see cref="IStillDecoder"/> seam, with the Skia call taken out and nothing else: it produces
/// a real <see cref="StillFrame"/> over a real <see cref="BudgetedBuffer"/>, at exactly the size
/// <c>StillDecoder</c> would have produced for a source of the size the test declared
/// (<see cref="DecodeGeometry.Fit"/>, the same call the real decoder makes).
/// <para/>
/// So a <see cref="StillSource"/> built on this has the real wanted set, the real eviction, the real
/// reference counting and real native buffers that are really freed -- which is what a test of
/// AUDIT2.md § 1.1 needs and what <see cref="FakeStillSource"/>, which frees nothing, cannot give.
/// </summary>
internal sealed class SizedDecoder : IStillDecoder
{
    private readonly Dictionary<string, (int Width, int Height)> _sources = new();

    public SizedDecoder(DecodeBudget budget) => Budget = budget;

    public DecodeBudget Budget { get; }

    /// <summary>Counts every decode, so a test can tell a re-decode from a cache hit.</summary>
    public int Decodes;

    /// <summary>Declares the on-disk size of one file. Unknown files decode as 1000 x 1000.</summary>
    public SizedDecoder WithSource(string id, int width, int height)
    {
        _sources[id] = (width, height);
        return this;
    }

    public DecodeResult Decode(string path, int paneW, int paneH)
    {
        Interlocked.Increment(ref Decodes);
        var id = Path.GetFileName(path);
        var (srcW, srcH) = _sources.TryGetValue(id, out var s) ? s : (1000, 1000);
        var (fitW, fitH) = DecodeGeometry.Fit(srcW, srcH, paneW, paneH);
        var buffer = Budget.Allocate((long)fitW * fitH * 4);
        return DecodeResult.Ok(new StillFrame(id, buffer, fitW, fitH, srcW, srcH, isPartial: false));
    }
}
