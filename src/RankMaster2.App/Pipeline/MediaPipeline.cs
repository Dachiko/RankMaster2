namespace RankMaster2;

/// <summary>
/// Sequential media loader. Implementation is the next phase; the depth knob is real.
/// </summary>
public sealed class MediaPipeline : IMediaPipeline
{
    public const int DefaultPrefetchPairs = 2;

    public MediaPipeline(int prefetchPairs = DefaultPrefetchPairs)
    {
        if (prefetchPairs < 0)
            throw new ArgumentOutOfRangeException(nameof(prefetchPairs));
        PrefetchPairs = prefetchPairs;
    }

    public int PrefetchPairs { get; }

    public void Show(MediaId left, MediaId right) =>
        throw new NotImplementedException("Pipeline show — see SPEC.md.");

    public void Enqueue(Pair pair) =>
        throw new NotImplementedException("Pipeline enqueue — see SPEC.md.");

    public void Release(MediaId id) =>
        throw new NotImplementedException("Pipeline release — see SPEC.md.");

    public void CancelWarmContaining(MediaId id) =>
        throw new NotImplementedException("Pipeline cancel — see SPEC.md.");
}
