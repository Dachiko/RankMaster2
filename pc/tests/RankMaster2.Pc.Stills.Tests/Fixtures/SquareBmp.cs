namespace RankMaster2.Pc.Stills.Tests.Fixtures;

/// <summary>
/// A square 24-bit BMP of any size, written by hand like <see cref="TinyBmp"/> but big enough to
/// matter to the decode budget. BMP is the right format for a memory test: like PNG it has no
/// sampled decode, so the decode buffer is always the full source -- which is exactly the shape of
/// file AUDIT2.md § 2.1 is about, without needing an encoder or the corpus.
/// </summary>
internal static class SquareBmp
{
    public static string WriteTo(string folder, string name, int size)
    {
        var path = Path.Combine(folder, name);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var rowBytes = size * 3;
        var padding = (4 - rowBytes % 4) % 4;
        var stride = rowBytes + padding;
        var pixelBytes = stride * (long)size;
        const int pixelOffset = 14 + 40;

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write((int)(pixelOffset + pixelBytes));
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(pixelOffset);

        writer.Write(40);            // BITMAPINFOHEADER
        writer.Write(size);          // width
        writer.Write(size);          // height, positive = bottom-up
        writer.Write((short)1);      // planes
        writer.Write((short)24);     // bits per pixel
        writer.Write(0);             // BI_RGB
        writer.Write((int)pixelBytes);
        writer.Write(2835);          // ~72 dpi
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        // One row's worth of pixels, written `size` times: a plain vertical gradient, so a decoded
        // frame that is silently all one colour would be visible to a test that looked.
        var row = new byte[stride];
        for (var y = 0; y < size; y++)
        {
            var shade = (byte)(y * 255 / Math.Max(1, size - 1));
            for (var x = 0; x < size; x++)
            {
                row[x * 3] = shade;              // blue
                row[x * 3 + 1] = (byte)(255 - shade);
                row[x * 3 + 2] = 0x40;
            }
            writer.Write(row);
        }

        writer.Flush();
        return path;
    }
}
