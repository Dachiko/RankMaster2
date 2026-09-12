using System.Text;

namespace RankMaster2.Server.Tests.Fixtures;

/// <summary>
/// A baseline (sequential DCT, Huffman) JPEG encoder, 4:4:4, written against ITU-T T.81 and the
/// JFIF note rather than against an imaging library — same reason as <see cref="PngWriter"/>.
///
/// It can attach an EXIF APP1 segment (so an orientation fixture is genuinely EXIF-rotated, not
/// merely named that way) and an ICC APP2 segment (so a wide-gamut fixture genuinely carries a
/// profile the server has to apply before it claims sRGB output).
/// </summary>
public static class JpegWriter
{
    /// <summary>EXIF orientation values. 1 is upright; 6 is "rotate 90° clockwise to display".</summary>
    public const ushort OrientationUpright = 1;
    public const ushort OrientationRotate90Cw = 6;

    /// <param name="exifOrientation">
    /// When set, an APP1/EXIF segment carrying only tag 0x0112. Orientation 6 means the stored
    /// pixels are W×H but the image displays as H×W — which is exactly what
    /// <c>GET /media/{id}/meta</c> must report (SERVER_SPEC.md § 12.1: width/height are "after EXIF
    /// orientation is applied").
    /// </param>
    /// <param name="iccProfile">An ICC profile for an APP2 segment (SERVER_SPEC.md § 12.3).</param>
    public static byte[] Rgb(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel,
                             int quality = 80, ushort? exifOrientation = null, byte[]? iccProfile = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        var luma = ScaleQuant(LuminanceQuant, quality);
        var chroma = ScaleQuant(ChrominanceQuant, quality);

        using var output = new MemoryStream();
        WriteMarker(output, 0xD8);                         // SOI
        WriteJfif(output);
        if (exifOrientation is { } orientation) WriteExif(output, orientation);
        if (iccProfile is { Length: > 0 }) WriteIcc(output, iccProfile);
        WriteQuantTables(output, luma, chroma);
        WriteFrameHeader(output, width, height);
        WriteHuffmanTables(output);
        WriteScanHeader(output);
        WriteScan(output, width, height, pixel, luma, chroma);
        WriteMarker(output, 0xD9);                         // EOI
        return output.ToArray();
    }

    // ---- segments -------------------------------------------------------------------------

    private static void WriteMarker(Stream s, byte marker)
    {
        s.WriteByte(0xFF);
        s.WriteByte(marker);
    }

    private static void WriteSegment(Stream s, byte marker, byte[] payload)
    {
        WriteMarker(s, marker);
        var length = payload.Length + 2;
        s.WriteByte((byte)(length >> 8));
        s.WriteByte((byte)length);
        s.Write(payload);
    }

    private static void WriteJfif(Stream s) => WriteSegment(s, 0xE0, new byte[]
    {
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0,
        1, 1,        // version 1.1
        0,           // density units: none
        0, 1, 0, 1,  // X and Y density
        0, 0         // no embedded thumbnail
    });

    /// <summary>APP1: "Exif\0\0" then a little-endian TIFF header with a one-entry IFD0.</summary>
    private static void WriteExif(Stream s, ushort orientation)
    {
        using var payload = new MemoryStream();
        payload.Write(Encoding.ASCII.GetBytes("Exif"));
        payload.WriteByte(0);
        payload.WriteByte(0);

        void U16(ushort v) { payload.WriteByte((byte)v); payload.WriteByte((byte)(v >> 8)); }
        void U32(uint v)
        {
            payload.WriteByte((byte)v); payload.WriteByte((byte)(v >> 8));
            payload.WriteByte((byte)(v >> 16)); payload.WriteByte((byte)(v >> 24));
        }

        U16(0x4949);   // "II" — little endian
        U16(42);       // the TIFF magic
        U32(8);        // IFD0 starts right after this header
        U16(1);        // one entry
        U16(0x0112);   // Orientation
        U16(3);        // SHORT
        U32(1);        // one value
        U16(orientation);
        U16(0);        // the value field is four bytes; a SHORT leaves two spare
        U32(0);        // no IFD1

        WriteSegment(s, 0xE1, payload.ToArray());
    }

    /// <summary>APP2: "ICC_PROFILE\0", chunk index, chunk count, then the profile.</summary>
    private static void WriteIcc(Stream s, byte[] profile)
    {
        const int maxChunk = 65519; // 65533 payload cap, less the 14-byte identifier block
        var chunks = (profile.Length + maxChunk - 1) / maxChunk;
        if (chunks > 255) throw new ArgumentException("ICC profile too large to embed.", nameof(profile));

        for (var i = 0; i < chunks; i++)
        {
            var offset = i * maxChunk;
            var size = Math.Min(maxChunk, profile.Length - offset);

            using var payload = new MemoryStream();
            payload.Write(Encoding.ASCII.GetBytes("ICC_PROFILE"));
            payload.WriteByte(0);
            payload.WriteByte((byte)(i + 1));
            payload.WriteByte((byte)chunks);
            payload.Write(profile, offset, size);
            WriteSegment(s, 0xE2, payload.ToArray());
        }
    }

    private static void WriteQuantTables(Stream s, int[] luma, int[] chroma)
    {
        using var payload = new MemoryStream();
        for (var table = 0; table < 2; table++)
        {
            payload.WriteByte((byte)table); // 8-bit precision, table id
            var values = table == 0 ? luma : chroma;
            for (var i = 0; i < 64; i++) payload.WriteByte((byte)values[ZigZag[i]]);
        }
        WriteSegment(s, 0xDB, payload.ToArray());
    }

    private static void WriteFrameHeader(Stream s, int width, int height)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(8);                       // sample precision
        payload.WriteByte((byte)(height >> 8));
        payload.WriteByte((byte)height);
        payload.WriteByte((byte)(width >> 8));
        payload.WriteByte((byte)width);
        payload.WriteByte(3);                       // three components
        // 4:4:4 throughout: no subsampling keeps the encoder honest and the decoder unambiguous.
        payload.Write(new byte[] { 1, 0x11, 0 });   // Y,  H=1 V=1, quant table 0
        payload.Write(new byte[] { 2, 0x11, 1 });   // Cb, H=1 V=1, quant table 1
        payload.Write(new byte[] { 3, 0x11, 1 });   // Cr
        WriteSegment(s, 0xC0, payload.ToArray());   // SOF0 — baseline
    }

    private static void WriteHuffmanTables(Stream s)
    {
        using var payload = new MemoryStream();
        Append(0x00, DcLumaBits, DcLumaValues);
        Append(0x10, AcLumaBits, AcLumaValues);
        Append(0x01, DcChromaBits, DcChromaValues);
        Append(0x11, AcChromaBits, AcChromaValues);
        WriteSegment(s, 0xC4, payload.ToArray());

        void Append(byte id, byte[] bits, byte[] values)
        {
            payload.WriteByte(id);   // high nibble: 0 = DC, 1 = AC. low nibble: table id.
            payload.Write(bits);
            payload.Write(values);
        }
    }

    private static void WriteScanHeader(Stream s)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(3);
        payload.Write(new byte[] { 1, 0x00 });  // Y  uses DC table 0, AC table 0
        payload.Write(new byte[] { 2, 0x11 });  // Cb uses DC table 1, AC table 1
        payload.Write(new byte[] { 3, 0x11 });  // Cr
        payload.Write(new byte[] { 0, 63, 0 }); // full spectral selection, no successive approximation
        WriteSegment(s, 0xDA, payload.ToArray());
    }

    // ---- entropy-coded data ---------------------------------------------------------------

    private static void WriteScan(Stream output, int width, int height,
                                  Func<int, int, (byte R, byte G, byte B)> pixel, int[] luma, int[] chroma)
    {
        var writer = new BitWriter(output);
        var basis = CosineBasis;

        var y = new double[64];
        var cb = new double[64];
        var cr = new double[64];
        var scratch = new double[64];
        var coefficients = new int[64];
        int previousDcY = 0, previousDcCb = 0, previousDcCr = 0;

        for (var blockY = 0; blockY < height; blockY += 8)
        for (var blockX = 0; blockX < width; blockX += 8)
        {
            for (var row = 0; row < 8; row++)
            for (var column = 0; column < 8; column++)
            {
                // Edge blocks replicate the last real pixel rather than padding with grey, so the
                // DCT of a partial block does not invent an edge that is not in the image.
                var sx = Math.Min(blockX + column, width - 1);
                var sy = Math.Min(blockY + row, height - 1);
                var (r, g, b) = pixel(sx, sy);
                var index = row * 8 + column;

                // JFIF YCbCr, level-shifted by -128 as baseline JPEG expects.
                y[index] = 0.299 * r + 0.587 * g + 0.114 * b - 128.0;
                cb[index] = -0.168736 * r - 0.331264 * g + 0.5 * b;
                cr[index] = 0.5 * r - 0.418688 * g - 0.081312 * b;
            }

            previousDcY = EncodeBlock(writer, y, scratch, coefficients, basis, luma, previousDcY,
                                      DcLumaCodes, AcLumaCodes);
            previousDcCb = EncodeBlock(writer, cb, scratch, coefficients, basis, chroma, previousDcCb,
                                       DcChromaCodes, AcChromaCodes);
            previousDcCr = EncodeBlock(writer, cr, scratch, coefficients, basis, chroma, previousDcCr,
                                       DcChromaCodes, AcChromaCodes);
        }

        writer.Flush();
    }

    private static int EncodeBlock(BitWriter writer, double[] samples, double[] scratch, int[] coefficients,
                                   double[] basis, int[] quant, int previousDc,
                                   (ushort Code, byte Length)[] dcCodes, (ushort Code, byte Length)[] acCodes)
    {
        ForwardDct(samples, scratch, basis);

        for (var i = 0; i < 64; i++)
        {
            var quantised = scratch[i] / quant[i];
            coefficients[i] = (int)Math.Round(quantised, MidpointRounding.AwayFromZero);
        }

        var dc = coefficients[0];
        var diff = dc - previousDc;
        var category = Category(diff);
        Emit(writer, dcCodes[category]);
        if (category > 0) writer.Write(Magnitude(diff, category), category);

        var runLength = 0;
        for (var i = 1; i < 64; i++)
        {
            var value = coefficients[ZigZag[i]];
            if (value == 0)
            {
                runLength++;
                continue;
            }

            while (runLength > 15)
            {
                Emit(writer, acCodes[0xF0]); // ZRL — sixteen zeroes
                runLength -= 16;
            }

            var size = Category(value);
            Emit(writer, acCodes[(runLength << 4) | size]);
            writer.Write(Magnitude(value, size), size);
            runLength = 0;
        }

        if (runLength > 0) Emit(writer, acCodes[0x00]); // EOB
        return dc;
    }

    private static void Emit(BitWriter writer, (ushort Code, byte Length) symbol)
    {
        if (symbol.Length == 0)
            throw new InvalidOperationException("No Huffman code for this symbol; the standard tables are incomplete.");
        writer.Write(symbol.Code, symbol.Length);
    }

    /// <summary>Number of significant bits, T.81 Table F.1.</summary>
    private static int Category(int value)
    {
        var magnitude = Math.Abs(value);
        var bits = 0;
        while (magnitude > 0) { bits++; magnitude >>= 1; }
        return bits;
    }

    /// <summary>Negative values are coded as the one's complement of their magnitude.</summary>
    private static int Magnitude(int value, int category) =>
        value > 0 ? value : value + (1 << category) - 1;

    /// <summary>Separable 2-D forward DCT: <c>B · f · Bᵀ</c> with the normalisation folded into B.</summary>
    private static void ForwardDct(double[] input, double[] output, double[] basis)
    {
        Span<double> rows = stackalloc double[64];

        for (var u = 0; u < 8; u++)
        for (var x = 0; x < 8; x++)
        {
            var sum = 0.0;
            for (var k = 0; k < 8; k++) sum += basis[u * 8 + k] * input[k * 8 + x];
            rows[u * 8 + x] = sum;
        }

        for (var u = 0; u < 8; u++)
        for (var v = 0; v < 8; v++)
        {
            var sum = 0.0;
            for (var k = 0; k < 8; k++) sum += rows[u * 8 + k] * basis[v * 8 + k];
            output[u * 8 + v] = sum;
        }
    }

    private static readonly double[] CosineBasis = BuildCosineBasis();

    private static double[] BuildCosineBasis()
    {
        var basis = new double[64];
        for (var u = 0; u < 8; u++)
        {
            var c = u == 0 ? Math.Sqrt(0.125) : 0.5;
            for (var x = 0; x < 8; x++)
                basis[u * 8 + x] = c * Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
        }
        return basis;
    }

    // ---- quantisation ---------------------------------------------------------------------

    /// <summary>The IJG quality curve over the Annex K tables.</summary>
    private static int[] ScaleQuant(byte[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;

        var scaled = new int[64];
        for (var i = 0; i < 64; i++)
            scaled[i] = Math.Clamp((table[i] * scale + 50) / 100, 1, 255);
        return scaled;
    }

    // Annex K.1 and K.2, in natural (row-major) order.
    private static readonly byte[] LuminanceQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    };

    private static readonly byte[] ChrominanceQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    };

    private static readonly int[] ZigZag =
    {
        0,  1,  8,  16, 9,  2,  3,  10,
        17, 24, 32, 25, 18, 11, 4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6,  7,  14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    // ---- the standard Huffman tables, Annex K.3 -------------------------------------------

    private static readonly byte[] DcLumaBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcLumaValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] DcChromaBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcChromaValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] AcLumaBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };

    private static readonly byte[] AcLumaValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    };

    private static readonly byte[] AcChromaBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

    private static readonly byte[] AcChromaValues =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    };

    private static readonly (ushort Code, byte Length)[] DcLumaCodes = BuildCodes(DcLumaBits, DcLumaValues);
    private static readonly (ushort Code, byte Length)[] AcLumaCodes = BuildCodes(AcLumaBits, AcLumaValues);
    private static readonly (ushort Code, byte Length)[] DcChromaCodes = BuildCodes(DcChromaBits, DcChromaValues);
    private static readonly (ushort Code, byte Length)[] AcChromaCodes = BuildCodes(AcChromaBits, AcChromaValues);

    /// <summary>Canonical Huffman assignment, T.81 Annex C: shortest codes first, in table order.</summary>
    private static (ushort Code, byte Length)[] BuildCodes(byte[] bits, byte[] values)
    {
        var table = new (ushort, byte)[256];
        ushort code = 0;
        var index = 0;

        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < bits[length - 1]; i++)
                table[values[index++]] = (code++, (byte)length);
            code <<= 1;
        }

        return table;
    }

    /// <summary>MSB-first bit packing with the 0xFF 0x00 stuffing an entropy-coded segment requires.</summary>
    private sealed class BitWriter(Stream output)
    {
        private int _buffer;
        private int _bitsHeld;

        public void Write(int value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                if (++_bitsHeld != 8) continue;

                var completed = (byte)_buffer;
                output.WriteByte(completed);
                if (completed == 0xFF) output.WriteByte(0x00);
                _buffer = 0;
                _bitsHeld = 0;
            }
        }

        /// <summary>Pad the final partial byte with 1s, as T.81 requires.</summary>
        public void Flush()
        {
            if (_bitsHeld == 0) return;
            Write(0xFF, 8 - _bitsHeld);
        }
    }
}
