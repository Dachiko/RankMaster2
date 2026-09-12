using System.Security.Cryptography;

namespace RankMaster2.Server.Security;

/// <summary>
/// Per-request identifier. ULID shape — 48 bits of millisecond timestamp then 80 bits of CSPRNG
/// output, Crockford base32, 26 characters — matching the examples in SERVER_SPEC.md § 4.
/// Sortable, opaque, and never derived from anything the caller supplied.
/// </summary>
internal static class RequestId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFF_FFFF_FFFFUL;

        Span<char> buffer = stackalloc char[26];
        for (var i = 9; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(timestamp & 31)];
            timestamp >>= 5;
        }

        // 80 random bits into the remaining 16 base32 characters.
        var bits = 0;
        var accumulator = 0UL;
        var next = 10;
        foreach (var b in random)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                buffer[next++] = Alphabet[(int)((accumulator >> bits) & 31)];
            }
        }

        return new string(buffer);
    }
}
