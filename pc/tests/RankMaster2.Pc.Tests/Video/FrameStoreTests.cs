namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Xunit;

/// <summary>D-video.md § 6.1 test 2: the one-pending-present coalescing flag and use-after-free
/// safety.</summary>
public class FrameStoreTests
{
    [Fact]
    public void OnDisplay_twice_before_one_Present_posts_once_and_shows_the_newest_frame()
    {
        var store = new FrameStore();
        store.Allocate(4, 4, 16); // 4 wide * 4 bytes/px, 4 lines — small, deterministic buffer

        var firstShouldPost = store.OnDisplay();
        var secondShouldPost = store.OnDisplay();

        Assert.True(firstShouldPost);
        Assert.False(secondShouldPost); // one pending present per surface: the second call coalesces

        var front = store.BeginPresent();
        Assert.NotNull(front);
        Assert.Equal(16 * 4, front!.Length);
    }

    [Fact]
    public void BeginPresent_clears_the_pending_flag_so_a_later_frame_posts_again()
    {
        var store = new FrameStore();
        store.Allocate(4, 4, 16);

        Assert.True(store.OnDisplay());
        store.BeginPresent();
        Assert.True(store.OnDisplay()); // the flag was cleared by BeginPresent — this one posts too
    }

    [Fact]
    public void Free_then_a_late_OnDisplay_does_not_write_or_crash()
    {
        var store = new FrameStore();
        store.Allocate(4, 4, 16);
        store.Free();

        var shouldPost = store.OnDisplay();

        Assert.False(shouldPost);
        Assert.Null(store.BeginPresent());
        Assert.False(store.IsAllocated);
    }

    [Fact]
    public void Allocate_after_Free_starts_clean()
    {
        var store = new FrameStore();
        store.Allocate(4, 4, 16);
        store.Free();

        store.Allocate(8, 8, 32);

        Assert.True(store.IsAllocated);
        Assert.Equal(8, store.Width);
        Assert.Equal(8, store.Height);
        Assert.Equal(32, store.Stride);
    }
}
