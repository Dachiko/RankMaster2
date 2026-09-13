using RankMaster2.Server.Media;
using RankMaster2.Server.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// The two things a decoder swap is most likely to break quietly, because both produce a perfectly
/// valid image either way: an embedded colour profile that is no longer applied, and an EXIF
/// orientation that is no longer honoured.
/// <para/>
/// SERVER_SPEC.md § 12.3: "EXIF orientation and the ICC profile are applied during decode, and the
/// output is sRGB." A server that skips the profile serves an oversaturated photograph; a server
/// that skips the orientation serves it on its side. Neither shows up as an error, so neither is
/// caught by a status-code test — these assert the pixels.
/// </summary>
public class StillColourAndOrientationTests(ITestOutputHelper output)
{
    private static StillRenderer NewRenderer() => new(new MediaOptions());

    private static SKBitmap Render(string sourceFile, byte[] bytes, int width)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-colour-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, sourceFile);
            File.WriteAllBytes(path, bytes);

            using var renderer = NewRenderer();
            using var rendered = new MemoryStream();
            renderer.RenderAsync(
                path,
                new StillVariant(width, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
                rendered,
                CancellationToken.None).GetAwaiter().GetResult();

            return SKBitmap.Decode(rendered.ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// The regression that matters. The fixture's pixels are stored as Adobe RGB, whose green
    /// primary is well outside sRGB's, so the same numbers mean different colours. Reading them as
    /// if they were already sRGB — which is what happens when the profile is ignored — is exactly
    /// the oversaturation bug this was fixed for once already in the desktop app.
    /// <para/>
    /// The experiment is the same colour rendered twice through the same pipeline, once tagged and
    /// once untagged. Identical resampling, identical encoder, identical everything but the
    /// profile, so the difference that comes out <i>is</i> the colour transform. Comparing against
    /// hand-computed sRGB values instead would test the colorimetry of the fixture's profile as
    /// much as the server, and a pure primary lands on the gamut boundary and clips, which hides
    /// the shift entirely — the colours below are mixed for that reason.
    /// <para/>
    /// The fixture is a flat colour on purpose. A dithered one would put a one-pixel-period pattern
    /// through a JPEG encoder, and that compression noise swamps what is being measured.
    /// </summary>
    [Theory]
    [InlineData(180, 120, 60, "an earth tone")]
    [InlineData(120, 180, 90, "foliage")]
    [InlineData(90, 140, 200, "a sky blue")]
    [InlineData(200, 170, 160, "a skin tone")]
    public void AWideGamutJpegIsConvertedToSrgb_NotPassedThroughOversaturated(byte r, byte g, byte b, string what)
    {
        using var tagged = Render("widegamut.jpg", FlatJpeg(r, g, b, IccProfiles.AdobeRgb1998()), 240);
        using var untagged = Render("plain.jpg", FlatJpeg(r, g, b, iccProfile: null), 240);

        var withProfile = Centre(tagged);
        var without = Centre(untagged);
        var shift = MeanChannelDifference(withProfile, without);

        output.WriteLine(
            $"{what}: stored ({r},{g},{b}) -> tagged ({withProfile.Red},{withProfile.Green},{withProfile.Blue}) " +
            $"vs untagged ({without.Red},{without.Green},{without.Blue}), mean shift {shift:F1}/255");

        Assert.True(
            shift > 5.0,
            "SERVER_SPEC.md § 12.3: the ICC profile is applied during decode and the output is sRGB. " +
            $"The same pixels tagged Adobe RGB and left untagged rendered {shift:F1}/255 apart, which is to say " +
            "the profile made no difference — a wide-gamut photograph is being served oversaturated.");
    }

    /// <summary>The same for a PNG, whose profile travels in an <c>iCCP</c> chunk rather than APP2.</summary>
    [Fact]
    public void AWideGamutPngIsAlsoConverted()
    {
        var flat = (byte)120;
        byte[] Png(bool tagged) => PngWriter.Rgb(240, 180, (_, _) => (flat, (byte)180, (byte)90),
            iccProfile: tagged ? IccProfiles.AdobeRgb1998() : null, wideGamutChromaticities: tagged);

        using var tagged = Render("widegamut.png", Png(tagged: true), 240);
        using var untagged = Render("plain.png", Png(tagged: false), 240);

        var shift = MeanChannelDifference(Centre(tagged), Centre(untagged));
        output.WriteLine($"png: tagged {Centre(tagged)} vs untagged {Centre(untagged)}, mean shift {shift:F1}/255");

        Assert.True(shift > 5.0, $"The PNG's embedded profile made no difference to the pixels ({shift:F1}/255).");
    }

    /// <summary>
    /// A file with no profile is already sRGB by convention, so conversion must be a no-op rather
    /// than a second gamma pass. This is the other half of the bug: converting twice is as wrong as
    /// not converting at all, and it would quietly wash out every ordinary photograph in the
    /// library.
    /// </summary>
    [Theory]
    [InlineData(180, 120, 60)]
    [InlineData(0, 200, 0)]
    [InlineData(0, 0, 200)]
    [InlineData(128, 128, 128)]
    [InlineData(255, 255, 255)]
    public void AnUntaggedSourceIsLeftAlone(byte r, byte g, byte b)
    {
        using var rendered = Render("plain.jpg", FlatJpeg(r, g, b, iccProfile: null), 240);

        var got = Centre(rendered);
        var shift = (Math.Abs(got.Red - r) + Math.Abs(got.Green - g) + Math.Abs(got.Blue - b)) / 3.0;
        output.WriteLine($"untagged ({r},{g},{b}) -> ({got.Red},{got.Green},{got.Blue}), mean shift {shift:F1}/255");

        Assert.True(
            shift < 4.0,
            $"An untagged source is already sRGB, so it should survive the round trip. ({r},{g},{b}) came out as " +
            $"({got.Red},{got.Green},{got.Blue}), a mean shift of {shift:F1}/255 — a colour transform is being " +
            "applied where none is called for, which double-converts every ordinary photograph.");
    }

    /// <summary>
    /// EXIF orientation 6 is a phone held upright: stored landscape, displayed portrait. Skia
    /// reports the origin on <c>SKCodec.EncodedOrigin</c> but never applies it during decode, so
    /// this is the check that the renderer does it rather than assuming the decoder did.
    /// </summary>
    [Fact]
    public void ExifOrientationIsAppliedToThePixels()
    {
        using var rendered = Render("rotated.jpg", MediaFixtures.ExifRotatedJpeg(), 720);

        output.WriteLine($"stored {MediaFixtures.ExifRotatedStored}, rendered {rendered.Width}x{rendered.Height}");

        // Stored 200x100 with orientation 6, so it displays 100x200 — and is never upscaled.
        Assert.Equal(MediaFixtures.ExifRotatedDisplayed.Width, rendered.Width);
        Assert.Equal(MediaFixtures.ExifRotatedDisplayed.Height, rendered.Height);
    }

    /// <summary>
    /// The header read that fills <c>MediaMeta</c> has to agree with the pixels the still endpoint
    /// delivers. Reporting one orientation and rendering another is worse than either mistake
    /// alone, because a client lays out from <c>meta</c>.
    /// </summary>
    [Fact]
    public void MetaAgreesWithThePixelsItDescribes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-orient-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "rotated.jpg");
            File.WriteAllBytes(path, MediaFixtures.ExifRotatedJpeg());

            using var renderer = NewRenderer();
            var identified = renderer.Identify(path);

            Assert.Equal(MediaFixtures.ExifRotatedDisplayed.Width, identified.Width);
            Assert.Equal(MediaFixtures.ExifRotatedDisplayed.Height, identified.Height);

            using var rendered = Render("rotated.jpg", MediaFixtures.ExifRotatedJpeg(), 720);
            Assert.Equal(identified.Width, rendered.Width);
            Assert.Equal(identified.Height, rendered.Height);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// All eight EXIF orientations produce the dimensions the tag calls for. Five to eight are the
    /// transposed ones, and getting the matrix wrong for those is the classic way a rotate becomes
    /// a mirror without anyone noticing.
    /// </summary>
    [Theory]
    [InlineData(1, 200, 100)]
    [InlineData(2, 200, 100)]
    [InlineData(3, 200, 100)]
    [InlineData(4, 200, 100)]
    [InlineData(5, 100, 200)]
    [InlineData(6, 100, 200)]
    [InlineData(7, 100, 200)]
    [InlineData(8, 100, 200)]
    public void EveryExifOrientationRendersAtItsDisplayedSize(int orientation, int expectedWidth, int expectedHeight)
    {
        var bytes = JpegWriter.Rgb(200, 100, MediaFixtures.Landscape, exifOrientation: (ushort)orientation);

        using var rendered = Render($"orient{orientation}.jpg", bytes, 720);

        Assert.Equal(expectedWidth, rendered.Width);
        Assert.Equal(expectedHeight, rendered.Height);
    }

    /// <summary>
    /// The orientation matrices, checked as arithmetic rather than through a decoder: each maps the
    /// stored top-left corner to where that corner belongs once displayed.
    /// </summary>
    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 0f, 0f)]
    [InlineData(SKEncodedOrigin.TopRight, 200f, 0f)]
    [InlineData(SKEncodedOrigin.BottomRight, 200f, 100f)]
    [InlineData(SKEncodedOrigin.BottomLeft, 0f, 100f)]
    [InlineData(SKEncodedOrigin.LeftTop, 0f, 0f)]
    [InlineData(SKEncodedOrigin.RightTop, 100f, 0f)]
    [InlineData(SKEncodedOrigin.RightBottom, 100f, 200f)]
    [InlineData(SKEncodedOrigin.LeftBottom, 0f, 200f)]
    public void OrientationMatricesPlaceTheStoredOrigin(SKEncodedOrigin origin, float x, float y)
    {
        var matrix = StillRenderer.OrientationMatrix(origin, 200, 100);
        var mapped = matrix.MapPoint(0, 0);

        Assert.Equal(x, mapped.X, 3);
        Assert.Equal(y, mapped.Y, 3);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A flat-colour JPEG at the highest quality the writer offers, so the encode/decode round
    /// trip changes the pixels as little as it can and any shift that remains is the colour
    /// transform.
    /// </summary>
    private static byte[] FlatJpeg(byte r, byte g, byte b, byte[]? iccProfile) =>
        JpegWriter.Rgb(240, 180, (_, _) => (r, g, b), quality: 100, iccProfile: iccProfile);

    private static SKColor Centre(SKBitmap bitmap) => bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

    private static double MeanChannelDifference(SKColor a, SKColor b) =>
        (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / 3.0;

}
