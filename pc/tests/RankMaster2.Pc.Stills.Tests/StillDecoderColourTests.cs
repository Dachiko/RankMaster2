using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Stills.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// Colour is a silent failure -- the picture is valid either way -- so it is pixel-asserted, not
/// status-asserted (plan section 3.2, "what 1.1.3 was").
/// </summary>
public class StillDecoderColourTests(ITestOutputHelper output)
{
    private static readonly string? Stills = Corpus.Directory("stills");

    private static StillFrame DecodeOne(string path, int paneW = 960, int paneH = 1080)
    {
        var budget = new DecodeBudget(64L * 1024 * 1024);
        var decoder = new StillDecoder(budget);
        var result = decoder.Decode(path, paneW, paneH);
        Assert.True(result.IsSuccess, $"{path}: expected Ready, got {result.Failure}: {result.Detail}");
        return result.Frame!;
    }

    private static (int R, int G, int B) BandCentre(StillFrame frame, int band)
    {
        var x = frame.Width / 2;
        var y = frame.Height * band / 4 + frame.Height / 8;
        var (b, g, r, _) = PixelReader.At(frame, x, y);
        return (r, g, b);
    }

    [Fact]
    public void The_wide_gamut_file_is_converted_and_the_untagged_twin_is_not()
    {
        if (Stills is null) { output.WriteLine("skipped: corpus not built"); return; }
        var taggedPath = Path.Combine(Stills, "wide_gamut.jpg");
        var untaggedPath = Path.Combine(Stills, "wide_gamut_untagged.jpg");
        if (!File.Exists(taggedPath) || !File.Exists(untaggedPath))
        {
            output.WriteLine("skipped: wide_gamut files not in the corpus");
            return;
        }

        using var codecStream = File.OpenRead(taggedPath);
        using var codec = SKCodec.Create(codecStream)!;
        if (codec.Info.ColorSpace is null)
        {
            output.WriteLine("skipped: Skia did not parse this profile; see the report on LUT-based profiles");
            return;
        }
        if (codec.Info.ColorSpace.IsSrgb)
        {
            output.WriteLine("skipped: the corpus file's profile is sRGB, so there is nothing to convert");
            return;
        }

        var tagged = DecodeOne(taggedPath);
        var untagged = DecodeOne(untaggedPath);
        try
        {
            Assert.Equal((tagged.Width, tagged.Height), (untagged.Width, untagged.Height));

            var difference = PixelReader.MeanDifference(tagged, untagged);
            output.WriteLine($"tagged vs untagged mean difference: {difference:F1}/255");
            Assert.True(difference > 8.0, $"expected > 8/255, got {difference:F2}/255 -- the profile does not look converted.");
        }
        finally
        {
            tagged.Release();
            untagged.Release();
        }

        // Decode the tagged file a second time with a null destination colour space -- the raw
        // stored numbers, untransformed -- and assert they match the untagged file's frame. That
        // proves the difference above IS the colour transform and nothing else.
        var rawInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, colorspace: null);
        using var rawBitmap = new SKBitmap();
        rawBitmap.TryAllocPixels(rawInfo);
        codec.GetPixels(rawInfo, rawBitmap.GetPixels());

        var untaggedRaw = DecodeOne(untaggedPath);
        try
        {
            double total = 0;
            var count = 0;
            for (var band = 0; band < 4; band++)
            {
                var x = rawBitmap.Width / 2;
                var y = rawBitmap.Height * band / 4 + rawBitmap.Height / 8;
                var raw = rawBitmap.GetPixel(x, y);
                var (ub, ug, ur, _) = PixelReader.At(untaggedRaw, x * untaggedRaw.Width / rawBitmap.Width, y * untaggedRaw.Height / rawBitmap.Height);
                total += Math.Abs(raw.Red - ur) + Math.Abs(raw.Green - ug) + Math.Abs(raw.Blue - ub);
                count += 3;
            }
            var rawDifference = total / count;
            output.WriteLine($"raw (no colour space) vs untagged: {rawDifference:F2}/255");
            Assert.True(rawDifference < 2.0, $"raw stored numbers should match the untagged file within 2/255, got {rawDifference:F2}/255");
        }
        finally
        {
            untaggedRaw.Release();
        }
    }

    [Fact]
    public void An_untagged_file_is_left_alone()
    {
        if (Stills is null) { output.WriteLine("skipped: corpus not built"); return; }
        var path = Path.Combine(Stills, "wide_gamut_untagged.jpg");
        if (!File.Exists(path)) { output.WriteLine("skipped: wide_gamut_untagged.jpg not in the corpus"); return; }

        var frame = DecodeOne(path);
        try
        {
            (int R, int G, int B)[] expected =
            [
                (120, 180, 90),
                (90, 140, 200),
                (180, 120, 60),
                (150, 90, 170),
            ];

            for (var band = 0; band < 4; band++)
            {
                var (r, g, b) = BandCentre(frame, band);
                output.WriteLine($"band {band}: got ({r},{g},{b}), expected {expected[band]}");
                Assert.True(Math.Abs(r - expected[band].R) <= 3, $"band {band} R off by more than 3");
                Assert.True(Math.Abs(g - expected[band].G) <= 3, $"band {band} G off by more than 3");
                Assert.True(Math.Abs(b - expected[band].B) <= 3, $"band {band} B off by more than 3");
            }
        }
        finally
        {
            frame.Release();
        }
    }

    [Fact]
    public void Cmyk_and_grayscale_agree_with_the_rgb_encodings()
    {
        if (Stills is null) { output.WriteLine("skipped: corpus not built"); return; }
        var cmykPath = Path.Combine(Stills, "cmyk.jpg");
        var grayscalePath = Path.Combine(Stills, "grayscale.jpg");
        var progressivePath = Path.Combine(Stills, "progressive.jpg");
        var lossyWebpPath = Path.Combine(Stills, "lossy.webp");
        if (!File.Exists(cmykPath) || !File.Exists(grayscalePath) || !File.Exists(progressivePath) || !File.Exists(lossyWebpPath))
        {
            output.WriteLine("skipped: cmyk/grayscale/progressive/lossy.webp not all in the corpus");
            return;
        }

        var cmyk = DecodeOne(cmykPath);
        var grayscale = DecodeOne(grayscalePath);
        var progressive = DecodeOne(progressivePath);
        var lossyWebp = DecodeOne(lossyWebpPath);
        try
        {
            var cmykDiff = PixelReader.MeanDifference(cmyk, progressive);
            output.WriteLine($"cmyk vs progressive: {cmykDiff:F1}/255");
            Assert.True(cmykDiff < 8.0, $"cmyk.jpg should decode to the same picture as progressive.jpg, got {cmykDiff:F2}/255");

            var webpDiff = PixelReader.MeanDifference(lossyWebp, progressive);
            output.WriteLine($"lossy.webp vs progressive: {webpDiff:F1}/255");
            Assert.True(webpDiff < 8.0, $"lossy.webp should decode to the same picture as progressive.jpg, got {webpDiff:F2}/255");

            for (var iy = 1; iy < 10; iy++)
            {
                for (var ix = 1; ix < 10; ix++)
                {
                    var (b, g, r, _) = PixelReader.At(grayscale, grayscale.Width * ix / 10, grayscale.Height * iy / 10);
                    Assert.True(r == g && g == b, $"grayscale.jpg pixel at ({ix},{iy}) is not neutral: R={r} G={g} B={b}");
                }
            }
        }
        finally
        {
            cmyk.Release();
            grayscale.Release();
            progressive.Release();
            lossyWebp.Release();
        }
    }
}
