using RankMaster2.Server.Tests.Fixtures;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// A 48 MP JPEG — 8000 × 6000, the size of a current phone's top setting and the size the desktop
/// app was measured on.
/// <para/>
/// It is written by <see cref="JpegWriter"/>, which encodes MCU row by MCU row from a pixel
/// callback and therefore never holds the image, so producing a 48 MP fixture costs a few MB. That
/// matters: a fixture that itself needed 144 MB would make the thing under test unmeasurable.
/// <para/>
/// The file is cached in the temp directory across runs, keyed by its dimensions, because encoding
/// it takes a few seconds.
/// </summary>
public static class LargeImageFixture
{
    public const int Width = 8000;
    public const int Height = 6000;

    /// <summary>Megapixels, for the arithmetic in the assertions.</summary>
    public const double Megapixels = Width * (double)Height / 1_000_000;

    /// <summary>Bytes a full RGB decode of this image needs, in one contiguous buffer: 144 MB.</summary>
    public const long FullDecodeBytes = (long)Width * Height * 3;

    private static readonly Lazy<string> Cached = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Path => Cached.Value;

    private static string Create()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rm2-media-tests");
        Directory.CreateDirectory(dir);

        var path = System.IO.Path.Combine(dir, $"large-{Width}x{Height}.jpg");
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
            return path;

        // Detail at the pixel level, so the JPEG does not collapse into a few bytes and the
        // decoder has real entropy-coded data to walk.
        var bytes = JpegWriter.Rgb(Width, Height, Noisy, quality: 80);

        var temp = path + ".tmp" + Environment.ProcessId;
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private static (byte R, byte G, byte B) Noisy(int x, int y)
    {
        var h = HashCode.Combine(x / 7, y / 5, (x + y) / 3);
        return ((byte)(60 + (h & 0x7F)), (byte)(90 + ((h >> 8) & 0x5F)), (byte)(40 + ((h >> 16) & 0x6F)));
    }
}
