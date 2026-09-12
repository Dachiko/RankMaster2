using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RankMaster2.Cli;

/// <summary>
/// Makes a throwaway folder of real images, so <c>rm2ctl cycle</c> can drive a full ranking run on a
/// machine with no photos on it.
///
/// The PNG encoder below is deliberately self-contained — RFC 2083 plus
/// <see cref="DeflateStream"/> — rather than pulled from an imaging library. rm2ctl is the tool you
/// reach for when the server is misbehaving, and a diagnostic tool that drags in an image stack has
/// more ways to fail than the thing it is diagnosing.
/// </summary>
public static class ScratchLibrary
{
    /// <summary>Create a folder of <paramref name="count"/> distinct PNGs and return its path.</summary>
    public static string Create(int count = 6)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm2ctl-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var names = new[]
        {
            "alpha.png", "bravo.png", "charlie.png", "delta.png", "echo.png", "foxtrot.png",
            "golf.png", "hotel.png", "india.png", "juliet.png"
        };

        for (var i = 0; i < count; i++)
        {
            var name = i < names.Length ? names[i] : $"item {i + 1}.png";
            var shade = i;
            File.WriteAllBytes(Path.Combine(folder, name),
                EncodePng(120 + i * 20, 90 + i * 10, (x, y) => (
                    (byte)((x * 3 + shade * 40) % 256),
                    (byte)((y * 5 + shade * 25) % 256),
                    (byte)((x + y + shade * 60) % 256))));
        }

        return folder;
    }

    public static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder that will not delete is not worth failing the run over.
        }
    }

    // ---- a small PNG encoder -------------------------------------------------------------------

    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    public static byte[] EncodePng(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // truecolour RGB
        Chunk(output, "IHDR", header);

        var raw = new byte[height * (1 + width * 3)];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                raw[at++] = r;
                raw[at++] = g;
                raw[at++] = b;
            }
        }

        Chunk(output, "IDAT", Zlib(raw));
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var wrapped = new MemoryStream();
        wrapped.WriteByte(0x78);
        wrapped.WriteByte(0x01);
        using (var deflate = new DeflateStream(wrapped, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
        wrapped.Write(adler);
        return wrapped.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        output.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var c = 0xFFFFFFFFu;
        foreach (var value in first) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        foreach (var value in second) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
