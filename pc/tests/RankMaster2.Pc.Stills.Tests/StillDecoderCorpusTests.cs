using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Stills.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// StillDecoder against the real corpus (tests/corpus/media). Every test begins with the server's
/// skip idiom (plan section 6): if the corpus is not built, write "skipped" and return.
/// </summary>
public class StillDecoderCorpusTests(ITestOutputHelper output)
{
    private static readonly string? Stills = Corpus.Directory("stills");

    private static (int Width, int Height) IdentifySourceSize(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream)!;
        return DecodeGeometry.SwapsAxes(codec.EncodedOrigin)
            ? (codec.Info.Height, codec.Info.Width)
            : (codec.Info.Width, codec.Info.Height);
    }

    [SkippableFact]
    public void The_bomb_is_decoded_inside_a_small_budget()
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, "bomb_40mp.jpg");
        Skip.If(!File.Exists(path), "bomb_40mp.jpg not in the corpus");

        var budget = new DecodeBudget(32L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var result = decoder.Decode(path, 960, 1080);
        Assert.True(result.IsSuccess, $"expected Ready, got {result.Failure}: {result.Detail}");
        var frame = result.Frame!;

        output.WriteLine($"bomb_40mp.jpg -> {frame.Width}x{frame.Height}, peak {budget.PeakBytes / 1024.0 / 1024.0:F2} MB");

        Assert.Equal(960, frame.Width);
        Assert.Equal(600, frame.Height);
        Assert.True(budget.PeakBytes < 32L * 1024 * 1024, $"peak was {budget.PeakBytes / 1024 / 1024} MB");

        frame.Release();
        Assert.Equal(0, budget.LiveBytes);
    }

    [SkippableTheory]
    [InlineData("landscape")]
    [InlineData("portrait")]
    public void All_eight_orientations_of_one_shape_give_one_displayed_size(string shape)
    {
        Skip.If(Stills is null, "corpus not built");

        var budget = new DecodeBudget(128L * 1024 * 1024);
        var decoder = new StillDecoder(budget);
        var seen = new List<(int Orientation, int W, int H, int SrcW, int SrcH)>();

        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var path = Path.Combine(Stills!, $"exif_{shape}_{orientation}.jpg");
            if (!File.Exists(path)) continue;

            var result = decoder.Decode(path, 960, 1080);
            Assert.True(result.IsSuccess, $"exif_{shape}_{orientation}: expected Ready, got {result.Failure}: {result.Detail}");
            var frame = result.Frame!;
            seen.Add((orientation, frame.Width, frame.Height, frame.SourceWidth, frame.SourceHeight));
            frame.Release();
        }

        Skip.If(seen.Count == 0, $"no exif_{shape}_* files in the corpus");
        Assert.Equal(8, seen.Count);

        var distinctSizes = seen.Select(s => (s.W, s.H)).Distinct().ToArray();
        var distinctSources = seen.Select(s => (s.SrcW, s.SrcH)).Distinct().ToArray();
        output.WriteLine($"{shape}: displayed {string.Join(", ", distinctSizes)}; source {string.Join(", ", distinctSources)}");

        Assert.True(distinctSizes.Length == 1, $"the eight {shape} orientations rendered at {distinctSizes.Length} different sizes: " +
            string.Join(", ", seen.Select(s => $"{s.Orientation}->{s.W}x{s.H}")));
        Assert.True(distinctSources.Length == 1, $"the eight {shape} orientations reported {distinctSources.Length} different source sizes");

        var expectedSource = shape == "landscape" ? (1800, 1200) : (1200, 1800);
        Assert.Equal(expectedSource, distinctSources[0]);
    }

    [SkippableTheory]
    [InlineData("landscape")]
    [InlineData("portrait")]
    public void Orientation_is_applied_to_the_pixels_not_just_the_size(string shape)
    {
        Skip.If(Stills is null, "corpus not built");

        var basePath = Path.Combine(Stills!, $"exif_{shape}_1.jpg");
        Skip.If(!File.Exists(basePath), $"exif_{shape}_1.jpg not in the corpus");

        var budget = new DecodeBudget(128L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var baseResult = decoder.Decode(basePath, 960, 1080);
        Assert.True(baseResult.IsSuccess);
        var baseFrame = baseResult.Frame!;

        try
        {
            for (var orientation = 2; orientation <= 8; orientation++)
            {
                var path = Path.Combine(Stills!, $"exif_{shape}_{orientation}.jpg");
                if (!File.Exists(path)) continue;

                var result = decoder.Decode(path, 960, 1080);
                Assert.True(result.IsSuccess, $"exif_{shape}_{orientation}: expected Ready, got {result.Failure}");
                var frame = result.Frame!;
                try
                {
                    var difference = PixelReader.MeanDifference(baseFrame, frame);
                    output.WriteLine($"exif_{shape}_{orientation} vs exif_{shape}_1: mean difference {difference:F1}/255");
                    Assert.True(difference < 6.0,
                        $"exif_{shape}_{orientation} differs from exif_{shape}_1 by {difference:F1}/255 once both are upright -- orientation was not applied to the pixels.");
                }
                finally
                {
                    frame.Release();
                }
            }
        }
        finally
        {
            baseFrame.Release();
        }
    }

    public static IEnumerable<object[]> RealEncodings()
    {
        yield return ["progressive.jpg"];
        yield return ["grayscale.jpg"];
        yield return ["cmyk.jpg"];
        yield return ["interlaced.png"];
        yield return ["lossy.webp"];
        yield return ["lossless.webp"];
        yield return ["animated.gif"];
        yield return ["tiny.png"];
        yield return ["photo_cat.jpg"];
        yield return ["photo_large.jpg"];
        yield return ["wide_gamut.jpg"];
    }

    [SkippableTheory]
    [MemberData(nameof(RealEncodings))]
    public void Every_real_encoding_gives_a_frame_of_the_expected_size(string name)
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, name);
        Skip.If(!File.Exists(path), $"{name} not in the corpus");

        AssertExpectedSize(path, name);
    }

    [Fact]
    public void Every_real_encoding_gives_a_frame_of_the_expected_size_bmp()
    {
        var path = TinyBmp.WriteToTempFile();
        try
        {
            AssertExpectedSize(path, "TinyBmp");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void AssertExpectedSize(string path, string name)
    {
        var (srcW, srcH) = IdentifySourceSize(path);
        var budget = new DecodeBudget(128L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var result = decoder.Decode(path, 960, 1080);
        Assert.True(result.IsSuccess, $"{name}: expected Ready, got {result.Failure}: {result.Detail}");
        var frame = result.Frame!;

        output.WriteLine($"{name}: source {srcW}x{srcH} -> {frame.Width}x{frame.Height}");

        Assert.False(frame.IsPartial, $"{name} should not be partial");
        Assert.Equal(DecodeGeometry.Fit(srcW, srcH, 960, 1080), (frame.Width, frame.Height));
        Assert.Equal(frame.Width * 4, frame.RowBytes);
        Assert.Equal(frame.ByteSize, budget.LiveBytes);

        frame.Release();
        Assert.Equal(0, budget.LiveBytes);
    }

    [SkippableTheory]
    [InlineData("tiny.png", 320, 240)]
    [InlineData("wide_gamut.jpg", 600, 400)]
    public void A_small_image_is_delivered_at_source_size(string name, int expectedW, int expectedH)
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, name);
        Skip.If(!File.Exists(path), $"{name} not in the corpus");

        var budget = new DecodeBudget(64L * 1024 * 1024);
        var decoder = new StillDecoder(budget);
        var result = decoder.Decode(path, 960, 1080);
        Assert.True(result.IsSuccess);
        var frame = result.Frame!;

        Assert.Equal(expectedW, frame.Width);
        Assert.Equal(expectedH, frame.Height);
        Assert.Equal(frame.SourceWidth, frame.Width);
        Assert.Equal(frame.SourceHeight, frame.Height);

        frame.Release();
    }

    [SkippableFact]
    public void A_png_decodes_at_full_size_then_downscales()
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, "interlaced.png");
        Skip.If(!File.Exists(path), "interlaced.png not in the corpus");

        var budget = new DecodeBudget(16L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var result = decoder.Decode(path, 480, 360);
        Assert.True(result.IsSuccess, $"expected Ready, got {result.Failure}: {result.Detail}");
        var frame = result.Frame!;

        output.WriteLine($"interlaced.png -> {frame.Width}x{frame.Height}, peak {budget.PeakBytes / 1024.0 / 1024.0:F2} MB");

        var peakMb = budget.PeakBytes / 1024.0 / 1024.0;
        Assert.True(peakMb is > 7.3 and < 9.0, $"peak was {peakMb:F2} MB, expected between 7.3 and 9 MB (full 1600x1200 buffer + fit)");

        frame.Release();
    }

    [SkippableFact]
    public void The_first_frame_of_an_animated_gif_is_used()
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, "animated.gif");
        Skip.If(!File.Exists(path), "animated.gif not in the corpus");

        var budget = new DecodeBudget(16L * 1024 * 1024);
        var decoder = new StillDecoder(budget);
        var result = decoder.Decode(path, 960, 1080);
        Assert.True(result.IsSuccess);
        var frame = result.Frame!;

        Assert.Equal(240, frame.Width);
        Assert.Equal(180, frame.Height);

        var (b, g, r, _) = PixelReader.At(frame, frame.Width / 2, frame.Height / 2);
        output.WriteLine($"animated.gif centre pixel: #{r:x2}{g:x2}{b:x2}");
        AssertNear(0x75, r, 4);
        AssertNear(0x3d, g, 4);
        AssertNear(0x13, b, 4);

        frame.Release();
    }

    private static void AssertNear(int expected, int actual, int tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected:x2} +/- {tolerance}, got {actual:x2}");

    [SkippableFact]
    public void Alpha_is_premultiplied()
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, "lossless.webp");
        Skip.If(!File.Exists(path), "lossless.webp not in the corpus");

        var budget = new DecodeBudget(16L * 1024 * 1024);
        var decoder = new StillDecoder(budget);
        var result = decoder.Decode(path, 960, 1080);
        Assert.True(result.IsSuccess, $"expected Ready, got {result.Failure}: {result.Detail}");
        var frame = result.Frame!;

        for (var iy = 1; iy < 5; iy++)
        {
            for (var ix = 1; ix < 5; ix++)
            {
                var (b, g, r, a) = PixelReader.At(frame, frame.Width * ix / 5, frame.Height * iy / 5);
                Assert.Equal(255, a);
                Assert.True(b <= a && g <= a && r <= a);
            }
        }

        frame.Release();
    }

    [SkippableFact]
    public void A_pane_change_is_honoured_at_the_next_decode()
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, "photo_large.jpg");
        Skip.If(!File.Exists(path), "photo_large.jpg not in the corpus");

        var budget = new DecodeBudget(64L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var first = decoder.Decode(path, 960, 1080);
        Assert.True(first.IsSuccess);
        Assert.Equal((960, 645), (first.Frame!.Width, first.Frame.Height));
        first.Frame.Release();

        var second = decoder.Decode(path, 1920, 2160);
        Assert.True(second.IsSuccess);
        Assert.Equal((1920, 1289), (second.Frame!.Width, second.Frame.Height));
        second.Frame.Release();
    }
}
