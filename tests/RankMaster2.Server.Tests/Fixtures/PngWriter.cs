using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RankMaster2.Server.Tests.Fixtures;

/// <summary>
/// A PNG encoder written against the format, not against an imaging library.
///
/// The fixtures have to be independent of whatever the server decodes with. If these bytes came out
/// of the same library the server reads them back with, a round-trip would prove only that the
/// library agrees with itself — an EXIF or ICC bug that cancels on both sides would stay invisible.
/// So: RFC 2083 by hand, System.IO.Compression for the deflate stream and nothing else.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Encode 8-bit RGB. <paramref name="iccProfile"/> is embedded as an iCCP chunk when given.</summary>
    public static byte[] Rgb(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel,
                            byte[]? iccProfile = null, bool wideGamutChromaticities = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        using var output = new MemoryStream();
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type 2 = truecolour RGB
        ihdr[10] = 0;  // deflate
        ihdr[11] = 0;  // adaptive filtering
        ihdr[12] = 0;  // no interlace
        WriteChunk(output, "IHDR", ihdr);

        // Wide-gamut declared the light way: cHRM primaries plus gAMA. A decoder that honours
        // colour at all has to notice these, and they cost no ICC parsing.
        if (wideGamutChromaticities)
        {
            var chrm = new byte[32];
            // Display P3 primaries, in PNG's 100000ths.
            WriteU32(chrm, 0, 31270); WriteU32(chrm, 4, 32900);   // white point D65
            WriteU32(chrm, 8, 68000); WriteU32(chrm, 12, 32000);  // red
            WriteU32(chrm, 16, 26500); WriteU32(chrm, 20, 69000); // green
            WriteU32(chrm, 24, 15000); WriteU32(chrm, 28, 6000);  // blue
            WriteChunk(output, "cHRM", chrm);

            var gama = new byte[4];
            WriteU32(gama, 0, 45455); // 1/2.2
            WriteChunk(output, "gAMA", gama);
        }

        if (iccProfile is { Length: > 0 })
        {
            using var iccp = new MemoryStream();
            iccp.Write(Encoding.ASCII.GetBytes("Fixture profile"));
            iccp.WriteByte(0);  // null-terminated profile name
            iccp.WriteByte(0);  // compression method 0 = deflate
            iccp.Write(Zlib(iccProfile));
            WriteChunk(output, "iCCP", iccp.ToArray());
        }

        // Raw scanlines, filter byte 0 in front of each. Filtering would compress better and prove
        // nothing extra.
        var raw = new byte[height * (1 + width * 3)];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0;
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                raw[at++] = r;
                raw[at++] = g;
                raw[at++] = b;
            }
        }

        WriteChunk(output, "IDAT", Zlib(raw));
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteU32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), value);

    /// <summary>A zlib stream: the two-byte header, raw deflate, then the Adler-32 of the input.</summary>
    private static byte[] Zlib(byte[] data)
    {
        using var wrapped = new MemoryStream();
        wrapped.WriteByte(0x78); // CM=8, CINFO=7 (32K window)
        wrapped.WriteByte(0x01); // FCHECK so that 0x7801 % 31 == 0, no dictionary, fastest
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

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
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
