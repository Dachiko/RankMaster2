namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Xunit;

/// <summary>D-video.md § 6.1 test 1: PaneFit / Align32, ported unchanged from the old app's
/// Fit/Align32.</summary>
public class PaneFitTests
{
    [Fact]
    public void Shrinks_4K_into_a_1080p_pane()
    {
        var (w, h) = PaneFit.Fit(3840, 2160, 1920, 2160);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void Never_upscales()
    {
        var (w, h) = PaneFit.Fit(1280, 720, 1920, 2160);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Theory]
    [InlineData(121, 81)]
    [InlineData(1, 1)]
    [InlineData(1921, 1)]
    public void Odd_sizes_become_even(int sourceWidth, int sourceHeight)
    {
        var (w, h) = PaneFit.Fit(sourceWidth, sourceHeight, 1920, 1080);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.True(w >= 2);
        Assert.True(h >= 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1920 * 4)]
    public void Align32_rounds_up_to_a_multiple_of_32(int size)
    {
        var aligned = PaneFit.Align32(size);
        Assert.Equal(0, aligned % 32);
        Assert.True(aligned >= size);
        Assert.True(aligned - size < 32);
    }
}
