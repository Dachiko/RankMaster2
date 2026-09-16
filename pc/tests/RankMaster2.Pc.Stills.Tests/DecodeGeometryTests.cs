using RankMaster2.Pc.Stills;
using SkiaSharp;
using Xunit;

namespace RankMaster2.Pc.Stills.Tests;

public class DecodeGeometryTests
{
    [Fact]
    public void Fit_never_upscales()
    {
        Assert.Equal((600, 400), DecodeGeometry.Fit(600, 400, 960, 1080));
    }

    [Fact]
    public void Fit_lands_on_the_binding_axis()
    {
        Assert.Equal((960, 645), DecodeGeometry.Fit(4390, 2948, 960, 1080));
        Assert.Equal((720, 1080), DecodeGeometry.Fit(1200, 1800, 960, 1080));
    }

    [Fact]
    public void Fit_caps_the_long_edge_at_4096()
    {
        Assert.Equal((4096, 2048), DecodeGeometry.Fit(10000, 5000, 8000, 8000));
    }

    [Theory]
    [InlineData(4390, 2948, 960, 960, 645)]
    [InlineData(1200, 1800, 960, 640, 960)]
    [InlineData(100, 100, 500, 100, 100)]
    public void FitLongEdge_matches_the_server(int width, int height, int target, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), DecodeGeometry.FitLongEdge(width, height, target));
    }

    [Fact]
    public void OrientationMatrices_are_identity_for_the_upright_origins()
    {
        Assert.Equal(SKMatrix.Identity, DecodeGeometry.OrientationMatrix(SKEncodedOrigin.TopLeft, 100, 200));
    }

    /// <summary>
    /// The server's own theory (StillRenderer's OrientationMatricesPlaceTheStoredOrigin): each
    /// matrix, applied to the four corners of the stored (width x height) box, places the pixel
    /// that TIFF orientation says is the stored top-left corner at the displayed top-left corner.
    /// Verified here by mapping the stored top-left corner (0,0) and asserting it lands where the
    /// orientation says the "first" stored pixel ends up in the displayed frame.
    /// </summary>
    [Theory]
    [InlineData(SKEncodedOrigin.TopRight, 0, 0, 100, 0)] // mirrored horizontally: stored (0,0) -> displayed (width,0)
    [InlineData(SKEncodedOrigin.BottomRight, 0, 0, 100, 200)] // rotated 180: stored (0,0) -> displayed (width,height)
    [InlineData(SKEncodedOrigin.BottomLeft, 0, 0, 0, 200)] // mirrored vertically: stored (0,0) -> displayed (0,height)
    [InlineData(SKEncodedOrigin.LeftTop, 0, 0, 0, 0)] // transpose about leading diagonal: stored (0,0) -> displayed (0,0)
    [InlineData(SKEncodedOrigin.RightTop, 0, 0, 200, 0)] // rotate 90 clockwise: stored (0,0) -> displayed (height,0) in output space
    [InlineData(SKEncodedOrigin.RightBottom, 0, 0, 200, 100)] // transpose about trailing diagonal
    [InlineData(SKEncodedOrigin.LeftBottom, 0, 0, 0, 100)] // rotate 90 anticlockwise
    public void OrientationMatrices_place_the_stored_origin(SKEncodedOrigin origin, float srcX, float srcY, float expectedX, float expectedY)
    {
        const int width = 100, height = 200;
        var matrix = DecodeGeometry.OrientationMatrix(origin, width, height);
        var mapped = matrix.MapPoint(srcX, srcY);
        Assert.Equal(expectedX, mapped.X, 3);
        Assert.Equal(expectedY, mapped.Y, 3);
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, false)]
    [InlineData(SKEncodedOrigin.TopRight, false)]
    [InlineData(SKEncodedOrigin.BottomRight, false)]
    [InlineData(SKEncodedOrigin.BottomLeft, false)]
    [InlineData(SKEncodedOrigin.LeftTop, true)]
    [InlineData(SKEncodedOrigin.RightTop, true)]
    [InlineData(SKEncodedOrigin.RightBottom, true)]
    [InlineData(SKEncodedOrigin.LeftBottom, true)]
    public void SwapsAxes_is_true_for_5_to_8_only(SKEncodedOrigin origin, bool expected)
    {
        Assert.Equal(expected, DecodeGeometry.SwapsAxes(origin));
    }

    [Fact]
    public void FrameCovers_a_pane_it_was_fit_for()
    {
        // photo_large-shaped source (4390x2948) fit to a 960x1080 pane gives 960x645 (see
        // Fit_lands_on_the_binding_axis above). That frame covers the pane it was fit for.
        Assert.True(DecodeGeometry.FrameCovers(960, 645, 4390, 2948, 960, 1080));
    }

    [Fact]
    public void FrameCovers_does_not_cover_a_much_bigger_pane()
    {
        Assert.False(DecodeGeometry.FrameCovers(960, 645, 4390, 2948, 1920, 2160));
    }

    [Fact]
    public void FrameCovers_a_source_sized_frame_covers_any_pane()
    {
        Assert.True(DecodeGeometry.FrameCovers(600, 400, 600, 400, 100, 100));
        Assert.True(DecodeGeometry.FrameCovers(600, 400, 600, 400, 10000, 10000));
    }
}
