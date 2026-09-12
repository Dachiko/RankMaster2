namespace RankMaster2.Server.Tests.Fixtures;

/// <summary>
/// Every byte the suite feeds the server, produced by code at run time. Nothing here is committed
/// as a binary: a checked-in JPEG is a fixture nobody can review, and the interesting properties of
/// these files — an EXIF orientation, an ICC profile, a truncation point — are exactly the ones a
/// reviewer cannot see in a blob.
///
/// The bytes are cached per process because the 4000×3000 fixture costs about a second and a half
/// to encode and several test classes want it.
/// </summary>
public static class MediaFixtures
{
    // ---- names ------------------------------------------------------------------------------
    //
    // The awkward ones are deliberate. SERVER_SPEC.md § 11.1 makes the filename the id and hands
    // the encoding problem to the URL layer, so the names below are the test of that clause.

    public const string PlainJpeg = "alpha.jpg";
    public const string SecondJpeg = "bravo.jpg";
    public const string ThirdJpeg = "charlie.jpg";
    public const string FourthJpeg = "delta.jpg";
    public const string PlainPng = "echo.png";
    public const string UppercaseExtension = "foxtrot.JPG";

    /// <summary>A space and a '#'. § 11.1 requires '%20', never '+', and '#' must be encoded.</summary>
    public const string SpacesAndHash = "beach day #2.jpg";

    /// <summary>Latin-1, Cyrillic, an em dash and a numero sign — multi-byte UTF-8 throughout.</summary>
    public const string Unicode = "Ärger am Fluß — фото №7.jpg";

    /// <summary>An astral-plane emoji: one code point, two UTF-16 units, four UTF-8 bytes.</summary>
    public const string Astral = "sunrise 🌅 over the sea.png";

    /// <summary>Names differing only by case. Two files on Linux, one on Windows — § 11.1.</summary>
    public const string MixedCase = "Sierra.jpg";
    public const string LowerCase = "sierra.jpg";

    public const string ExifRotated = "rotated.jpg";
    public const string WideGamut = "widegamut.jpg";
    public const string WideGamutPng = "widegamut.png";
    public const string Large = "huge.jpg";
    public const string ZeroByte = "empty.jpg";
    public const string Corrupt = "corrupt.jpg";
    public const string Truncated = "truncated.jpg";
    public const string Video = "clip.avi";
    public const string NotMedia = "notes.txt";

    /// <summary>The stored size of <see cref="ExifRotated"/>; it displays transposed.</summary>
    public static readonly (int Width, int Height) ExifRotatedStored = (200, 100);
    public static readonly (int Width, int Height) ExifRotatedDisplayed = (100, 200);

    public static readonly (int Width, int Height) LargeSize = (4000, 3000);

    // ---- bytes ------------------------------------------------------------------------------

    private static readonly Lazy<byte[]> LargeJpegBytes =
        new(() => JpegWriter.Rgb(LargeSize.Width, LargeSize.Height, Landscape, quality: 70),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> VideoBytes =
        new(() => AviWriter.MotionJpeg(96, 64, frameCount: 8, framesPerSecond: 8,
                                       (frame, x, y) => ((byte)(x * 3 + frame * 20), (byte)(y * 4), (byte)(frame * 30))),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> WideGamutProfile =
        new(IccProfiles.AdobeRgb1998, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A smooth gradient with a horizon — compresses like a photograph rather than like flat colour.</summary>
    public static (byte R, byte G, byte B) Landscape(int x, int y)
    {
        var horizon = 3 * 64;
        return y < horizon
            ? ((byte)(90 + x % 40), (byte)(140 + y % 60), (byte)(200 - y % 50))
            : ((byte)(60 + (x + y) % 70), (byte)(90 + x % 50), (byte)(40 + y % 30));
    }

    /// <summary>Colours outside sRGB's gamut, so a profile-aware conversion has to move them.</summary>
    private static (byte R, byte G, byte B) Saturated(int x, int y) =>
        ((x + y) % 3) switch
        {
            0 => (255, 0, 8),
            1 => (0, 255, 16),
            _ => (0, 24, 255)
        };

    public static byte[] Jpeg(int width, int height, int quality = 80) =>
        JpegWriter.Rgb(width, height, Landscape, quality);

    public static byte[] Png(int width, int height) => PngWriter.Rgb(width, height, Landscape);

    public static byte[] LargeJpeg() => LargeJpegBytes.Value;

    public static byte[] ExifRotatedJpeg() =>
        JpegWriter.Rgb(ExifRotatedStored.Width, ExifRotatedStored.Height, Landscape,
                       exifOrientation: JpegWriter.OrientationRotate90Cw);

    public static byte[] WideGamutJpeg() =>
        JpegWriter.Rgb(240, 180, Saturated, iccProfile: WideGamutProfile.Value);

    public static byte[] WideGamutPngBytes() =>
        PngWriter.Rgb(240, 180, Saturated, iccProfile: WideGamutProfile.Value, wideGamutChromaticities: true);

    public static byte[] SmallVideo() => VideoBytes.Value;

    /// <summary>
    /// A file that opens like a JPEG and then is not one: a valid SOI and JFIF segment followed by
    /// noise. It has to get past a header sniff to reach the decoder, which is where
    /// <c>422 media_decode_failed</c> (SERVER_SPEC.md § 5.5) is supposed to come from.
    /// </summary>
    public static byte[] CorruptJpeg()
    {
        var header = new byte[]
        {
            0xFF, 0xD8,                                     // SOI
            0xFF, 0xE0, 0x00, 0x10,                         // APP0, length 16
            0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00
        };

        var noise = new byte[2048];
        // A fixed seed keeps a failure reproducible; nothing here needs real entropy.
        new Random(20260912).NextBytes(noise);
        for (var i = 0; i < noise.Length; i++)
            if (noise[i] == 0xFF) noise[i] = 0x7F; // no accidental markers, so it cannot resynchronise

        return header.Concat(noise).ToArray();
    }

    /// <summary>A real JPEG cut off mid-scan: headers parse, the entropy data runs out.</summary>
    public static byte[] TruncatedJpeg()
    {
        var whole = Jpeg(320, 240);
        return whole.Take(whole.Length * 6 / 10).ToArray();
    }
}
