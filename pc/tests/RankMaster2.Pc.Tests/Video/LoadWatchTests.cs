namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Xunit;

/// <summary>D-video.md § 6.1 test 7: the fallback ladder of § 4.3, as pure state.
///
/// "Never with one surface" is not a fact <see cref="LoadWatch"/> itself can prove — it does not know
/// how many surfaces exist. It is proven structurally in <see cref="VideoEngine.LoadWatchTick"/>,
/// which only samples when exactly <c>VideoOptions.MaxLiveSurfaces</c> surfaces are live; nothing
/// calls <see cref="LoadWatch.Sample"/> otherwise.</summary>
public class LoadWatchTests
{
    private static VideoOptions Options() => new()
    {
        AlternateThreshold = 0.25,
        AlternateThresholdSamples = 3,
        MinSwapInterval = TimeSpan.FromSeconds(3)
    };

    [Fact]
    public void Rates_below_threshold_keep_both_playing()
    {
        var watch = new LoadWatch(Options());
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            var mode = watch.Sample(0.1, 0.1, TimeSpan.FromSeconds(8), now.AddSeconds(i));
            Assert.Equal(LoadWatchMode.Both, mode);
        }

        Assert.False(watch.IsAlternating);
    }

    [Fact]
    public void Three_consecutive_over_threshold_samples_engage_alternate_left_active()
    {
        var watch = new LoadWatch(Options());
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(LoadWatchMode.Both, watch.Sample(0.5, 0.1, TimeSpan.FromSeconds(8), now));
        Assert.Equal(LoadWatchMode.Both, watch.Sample(0.5, 0.1, TimeSpan.FromSeconds(8), now.AddSeconds(1)));
        var mode = watch.Sample(0.5, 0.1, TimeSpan.FromSeconds(8), now.AddSeconds(2));

        Assert.Equal(LoadWatchMode.AlternateLeftActive, mode);
        Assert.True(watch.IsAlternating);
    }

    [Fact]
    public void A_good_sample_in_between_resets_the_consecutive_count()
    {
        var watch = new LoadWatch(Options());
        var now = DateTimeOffset.UtcNow;

        watch.Sample(0.5, 0.1, TimeSpan.FromSeconds(8), now);
        watch.Sample(0.5, 0.1, TimeSpan.FromSeconds(8), now.AddSeconds(1));
        watch.Sample(0.1, 0.1, TimeSpan.FromSeconds(8), now.AddSeconds(2)); // good sample resets the streak
        var mode = watch.Sample(0.5, 0.1, TimeSpan.FromSeconds(8), now.AddSeconds(3));

        Assert.Equal(LoadWatchMode.Both, mode); // only one consecutive "over" sample so far
    }

    [Fact]
    public void Roles_swap_after_max_clip_length_and_min_swap_interval()
    {
        var watch = new LoadWatch(Options());
        var now = DateTimeOffset.UtcNow;
        watch.Sample(0.5, 0.1, TimeSpan.Zero, now);
        watch.Sample(0.5, 0.1, TimeSpan.Zero, now.AddSeconds(1));
        var mode = watch.Sample(0.5, 0.1, TimeSpan.Zero, now.AddSeconds(2));
        Assert.Equal(LoadWatchMode.AlternateLeftActive, mode);

        // Clip is 8s > MinSwapInterval(3s): no swap yet at +4s from the engage instant.
        mode = watch.Sample(0.0, 0.0, TimeSpan.FromSeconds(8), now.AddSeconds(6));
        Assert.Equal(LoadWatchMode.AlternateLeftActive, mode);

        // Past the 8s clip length: swap.
        mode = watch.Sample(0.0, 0.0, TimeSpan.FromSeconds(8), now.AddSeconds(11));
        Assert.Equal(LoadWatchMode.AlternateRightActive, mode);
    }

    [Fact]
    public void Alternate_mode_is_sticky_across_good_samples_until_Reset()
    {
        var watch = new LoadWatch(Options());
        var now = DateTimeOffset.UtcNow;
        watch.Sample(0.5, 0.1, TimeSpan.Zero, now);
        watch.Sample(0.5, 0.1, TimeSpan.Zero, now.AddSeconds(1));
        watch.Sample(0.5, 0.1, TimeSpan.Zero, now.AddSeconds(2));
        Assert.True(watch.IsAlternating);

        // Even a perfect sample afterwards does not fall back to Both — sticky for the session.
        var mode = watch.Sample(0.0, 0.0, TimeSpan.Zero, now.AddSeconds(2).AddTicks(1));
        Assert.NotEqual(LoadWatchMode.Both, mode);

        watch.Reset(); // LiveSurfaces == 0 — leaving the compare view
        Assert.Equal(LoadWatchMode.Both, watch.Mode);
        Assert.False(watch.IsAlternating);
    }
}
