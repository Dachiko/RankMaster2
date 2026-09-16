namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;

/// <summary>The synchronous <see cref="IUiThread"/> D-video.md § 6.1 calls for: "tests substitute a
/// synchronous IUiThread ... and drive every transition ... without LibVLC". <see cref="VideoEngine"/>
/// still runs its own real worker thread underneath (WarmUpAsync/Enqueue are genuinely asynchronous —
/// that is what proves the threading rules of § 4.6), so tests await/poll rather than assume a single
/// synchronous call stack end to end.</summary>
public sealed class SyncUiThread : IUiThread
{
    public bool IsOnThread => true;
    public void Post(Action action) => action();
}

internal static class TestWait
{
    public static async Task<bool> Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(5);
        }

        return condition();
    }
}
