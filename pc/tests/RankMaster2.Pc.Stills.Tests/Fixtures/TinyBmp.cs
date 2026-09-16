namespace RankMaster2.Pc.Stills.Tests.Fixtures;

/// <summary>
/// A 2x2 24-bit BMP written by hand (plan section 4: "the corpus has no .bmp"). Four distinct
/// corners -- red, green, blue, white -- bottom-up, BGR triplets, rows padded to a 4-byte
/// boundary, exactly as the format spec requires.
/// </summary>
internal static class TinyBmp
{
    public static byte[] Bytes()
    {
        // Bottom row (file order): bottom-left = blue, bottom-right = white.
        // Top row (file order, written second): top-left = red, top-right = green.
        byte[] bottomRow = [255, 0, 0, /**/ 255, 255, 255, /**/ 0, 0]; // BGR, BGR, padding
        byte[] topRow = [0, 0, 255, /**/ 0, 255, 0, /**/ 0, 0];

        var pixelData = new byte[bottomRow.Length + topRow.Length];
        Buffer.BlockCopy(bottomRow, 0, pixelData, 0, bottomRow.Length);
        Buffer.BlockCopy(topRow, 0, pixelData, bottomRow.Length, topRow.Length);

        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        const int pixelOffset = fileHeaderSize + infoHeaderSize;
        var fileSize = pixelOffset + pixelData.Length;

        var bytes = new byte[fileSize];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(pixelOffset);

        // BITMAPINFOHEADER
        writer.Write(infoHeaderSize);
        writer.Write(2); // width
        writer.Write(2); // height, positive = bottom-up
        writer.Write((short)1); // planes
        writer.Write((short)24); // bits per pixel
        writer.Write(0); // BI_RGB, no compression
        writer.Write(0); // image size, 0 is allowed for BI_RGB
        writer.Write(0); // X pixels per meter
        writer.Write(0); // Y pixels per meter
        writer.Write(0); // colours used
        writer.Write(0); // important colours

        writer.Write(pixelData);
        writer.Flush();
        return bytes;
    }

    public static string WriteToTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tiny_{Guid.NewGuid():N}.bmp");
        File.WriteAllBytes(path, Bytes());
        return path;
    }
}
