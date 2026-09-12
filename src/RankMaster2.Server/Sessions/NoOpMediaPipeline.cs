namespace RankMaster2.Server.Sessions;

/// <summary>
/// <c>LibraryActions</c> was written for the desktop shell and asks a pipeline to let go of a file
/// before moving it. The server decodes nothing at this layer, so there is nothing to release here;
/// the media layer hooks in through <see cref="SessionRegistry.ReleaseMedia"/> instead, which
/// <c>LibraryActions</c> calls as its UI-release callback.
/// </summary>
internal sealed class NoOpMediaPipeline : IMediaPipeline
{
    public int PrefetchPairs => SessionRegistry.DefaultPrefetchPairs;

    public void Show(MediaId left, MediaId right)
    {
    }

    public void Enqueue(Pair pair)
    {
    }

    public void Release(MediaId id)
    {
    }

    public void ReleaseAll()
    {
    }

    public void CancelWarmContaining(MediaId id)
    {
    }
}
