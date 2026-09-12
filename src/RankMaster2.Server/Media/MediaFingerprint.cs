using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RankMaster2.Server.Media;

/// <summary>
/// SERVER_SPEC.md § 12.2. One fingerprint, computed from the file <b>as it is at request time</b>,
/// feeds both <c>mediaVersion</c> and every <c>ETag</c>.
/// <code>
/// fingerprint  = SHA-256( utf8(id) || 0x00 || decimal(sizeBytes) || 0x00 || decimal(mtimeUtcTicks) )
/// mediaVersion = lowercase hex of fingerprint[0..8)    // 16 chars
/// ETag         = "\"" + variant + "-" + lowercase hex of fingerprint[0..16) + "\""
/// </code>
/// Stable across restarts and across instances because every input is on disk; it changes when the
/// bytes change, subject to the granularity limit § 12.5 records deliberately.
/// </summary>
public readonly struct MediaFingerprint
{
    private readonly byte[] _hash;

    private MediaFingerprint(byte[] hash) => _hash = hash;

    /// <summary>
    /// <paramref name="id"/> MUST be the on-disk spelling, not the spelling the client sent
    /// (§ 11.1.6). On a case-insensitive filesystem the two can differ, and an ETag that varied
    /// with the caller's casing would not be the same entity tag for the same bytes.
    /// </summary>
    public static MediaFingerprint Of(string id, long sizeBytes, long mtimeUtcTicks)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var sizeBytesText = Encoding.UTF8.GetBytes(sizeBytes.ToString(CultureInfo.InvariantCulture));
        var ticksText = Encoding.UTF8.GetBytes(mtimeUtcTicks.ToString(CultureInfo.InvariantCulture));

        var buffer = new byte[idBytes.Length + 1 + sizeBytesText.Length + 1 + ticksText.Length];
        var at = 0;
        idBytes.CopyTo(buffer, at);
        at += idBytes.Length;
        buffer[at++] = 0x00;
        sizeBytesText.CopyTo(buffer, at);
        at += sizeBytesText.Length;
        buffer[at++] = 0x00;
        ticksText.CopyTo(buffer, at);

        return new MediaFingerprint(SHA256.HashData(buffer));
    }

    public static MediaFingerprint Of(string id, FileInfo file) =>
        Of(id, file.Length, File.GetLastWriteTimeUtc(file.FullName).Ticks);

    /// <summary>16 lowercase hex chars — fingerprint[0..8).</summary>
    public string MediaVersion => Convert.ToHexString(_hash, 0, 8).ToLowerInvariant();

    /// <summary>32 lowercase hex chars — fingerprint[0..16). The body of every ETag.</summary>
    public string EntityHex => Convert.ToHexString(_hash, 0, 16).ToLowerInvariant();

    /// <summary>
    /// A strong entity tag. No <c>W/</c> prefix: § 12.2 says two responses carrying the same ETag
    /// are byte-identical forever, on every instance.
    /// </summary>
    public string ETag(string variant) => "\"" + variant + "-" + EntityHex + "\"";
}
