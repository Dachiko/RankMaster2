namespace RankMaster2.Server.Sessions;

/// <summary>
/// <c>LibraryActions</c> was written for the desktop shell and asks a pipeline to let go of a file
/// before moving it. The server decodes nothing at this layer, so every method here is empty and
/// that is the whole truth of it.
/// <para/>
/// It used to say the media layer hooked in through a <c>ReleaseMedia</c> callback on the registry.
/// It did not: that property was never assigned by anybody, and the media layer needs no warning
/// anyway because it opens every stream <c>FileShare.Delete</c> — a file can be moved out from under
/// an in-flight response. The callback and the claim are both gone (C13).
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
