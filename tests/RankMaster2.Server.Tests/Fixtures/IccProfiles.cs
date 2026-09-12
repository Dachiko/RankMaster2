using System.Buffers.Binary;
using System.Text;

namespace RankMaster2.Server.Tests.Fixtures;

/// <summary>
/// Builds a valid ICC v2 matrix/TRC display profile from its numbers, so the wide-gamut fixture
/// carries a real profile rather than a blob copied from somewhere.
///
/// Why this matters to the contract: SERVER_SPEC.md § 12.3 says the ICC profile is applied during
/// decode and "the output is sRGB". With a wide-gamut source that is a claim with teeth — saturated
/// colours have to move. A file with no profile could not tell the difference between a server that
/// converts and a server that ignores colour entirely.
/// </summary>
public static class IccProfiles
{
    /// <summary>
    /// Adobe RGB (1998): a genuinely wider gamut than sRGB, and — unlike Display P3 — its
    /// D50-adapted matrix has no negative components, which keeps strict parsers happy.
    /// </summary>
    public static byte[] AdobeRgb1998() => Build(
        description: "Fixture wide gamut (Adobe RGB 1998 primaries)",
        red: (0.60974, 0.31111, 0.01947),
        green: (0.20528, 0.62567, 0.06087),
        blue: (0.14919, 0.06322, 0.74457),
        gamma: 2.19921875);

    private static byte[] Build(string description,
                                (double X, double Y, double Z) red,
                                (double X, double Y, double Z) green,
                                (double X, double Y, double Z) blue,
                                double gamma)
    {
        // D50 is the profile connection space white point; it is not configurable.
        var whitePoint = (0.96420, 1.00000, 0.82491);

        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", TextDescription(description)),
            ("wtpt", Xyz(whitePoint)),
            ("rXYZ", Xyz(red)),
            ("gXYZ", Xyz(green)),
            ("bXYZ", Xyz(blue)),
            ("rTRC", Curve(gamma)),
            ("gTRC", Curve(gamma)),
            ("bTRC", Curve(gamma)),
            ("cprt", Text("Generated for Rank Master 2 tests; no rights reserved.")),
        };

        const int headerSize = 128;
        var tableSize = 4 + tags.Count * 12;
        var offset = Align(headerSize + tableSize);

        var table = new byte[tableSize];
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0), (uint)tags.Count);

        var body = new MemoryStream();
        for (var i = 0; i < tags.Count; i++)
        {
            var (signature, data) = tags[i];
            var entry = 4 + i * 12;
            Encoding.ASCII.GetBytes(signature).CopyTo(table, entry);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(entry + 4), (uint)(offset + body.Length));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(entry + 8), (uint)data.Length);

            body.Write(data);
            while (body.Length % 4 != 0) body.WriteByte(0); // every tag starts on a 4-byte boundary
        }

        var total = offset + (int)body.Length;
        var profile = new byte[total];

        // --- header ---
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(0), (uint)total);
        Ascii(profile, 4, "ADBE");                                        // preferred CMM
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000); // version 2.1
        Ascii(profile, 12, "mntr");                                       // display device
        Ascii(profile, 16, "RGB ");
        Ascii(profile, 20, "XYZ ");                                       // PCS
        // A fixed date keeps the profile — and so every fixture embedding it — byte-reproducible.
        WriteDateTime(profile, 24, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Ascii(profile, 36, "acsp");                                       // the profile file signature
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(64), 0);     // perceptual rendering intent
        WriteS15Fixed16(profile, 68, whitePoint.Item1);
        WriteS15Fixed16(profile, 72, whitePoint.Item2);
        WriteS15Fixed16(profile, 76, whitePoint.Item3);

        table.CopyTo(profile, headerSize);
        body.ToArray().CopyTo(profile, offset);
        return profile;
    }

    private static int Align(int value) => (value + 3) & ~3;

    private static void Ascii(byte[] target, int offset, string value) =>
        Encoding.ASCII.GetBytes(value).CopyTo(target, offset);

    private static void WriteDateTime(byte[] target, int offset, DateTime moment)
    {
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(at), (ushort)value);
        U16(offset, moment.Year);
        U16(offset + 2, moment.Month);
        U16(offset + 4, moment.Day);
        U16(offset + 6, moment.Hour);
        U16(offset + 8, moment.Minute);
        U16(offset + 10, moment.Second);
    }

    private static void WriteS15Fixed16(byte[] target, int offset, double value) =>
        BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(offset), (int)Math.Round(value * 65536.0));

    private static byte[] Xyz((double X, double Y, double Z) value)
    {
        var data = new byte[20];
        Ascii(data, 0, "XYZ ");
        WriteS15Fixed16(data, 8, value.X);
        WriteS15Fixed16(data, 12, value.Y);
        WriteS15Fixed16(data, 16, value.Z);
        return data;
    }

    /// <summary>A single-entry curve is a plain gamma, encoded u8Fixed8.</summary>
    private static byte[] Curve(double gamma)
    {
        var data = new byte[14];
        Ascii(data, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), (ushort)Math.Round(gamma * 256.0));
        return data;
    }

    private static byte[] Text(string value)
    {
        var ascii = Encoding.ASCII.GetBytes(value);
        var data = new byte[8 + ascii.Length + 1];
        Ascii(data, 0, "text");
        ascii.CopyTo(data, 8);
        return data;
    }

    /// <summary>
    /// v2's 'desc' tag: an ASCII copy, an empty Unicode copy and an empty Macintosh ScriptCode
    /// block. All three are structurally required even when only the first carries anything.
    /// </summary>
    private static byte[] TextDescription(string value)
    {
        var ascii = Encoding.ASCII.GetBytes(value);
        var asciiLength = ascii.Length + 1; // the count includes the terminator

        var data = new byte[8 + 4 + asciiLength + 4 + 4 + 2 + 1 + 67];
        Ascii(data, 0, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)asciiLength);
        ascii.CopyTo(data, 12);
        // Unicode language code and count, then ScriptCode code/count, all left zero.
        return data;
    }
}
