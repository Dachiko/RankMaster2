using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RankMaster2.Server.Media;

/// <summary>
/// SERVER_SPEC.md § 12.2 (pending an amendment — see the remarks below). One fingerprint,
/// computed from the file <b>as it is at request time</b>, feeds both <c>mediaVersion</c> and
/// every <c>ETag</c>.
/// <code>
/// fingerprint  = SHA-256( utf8(folder) || 0x00 || utf8(id) || 0x00 || decimal(sizeBytes) || 0x00 || decimal(mtimeUtcTicks) )
/// mediaVersion = lowercase hex of fingerprint[0..8)    // 16 chars
/// ETag         = "\"" + variant + "-" + lowercase hex of fingerprint[0..16) + "\""
/// </code>
/// Stable across restarts and across instances because every input is on disk; it changes when the
/// bytes change, subject to the granularity limit § 12.5 records deliberately.
/// <para/>
/// <b>Second audit, § 1.2.</b> <paramref name="folder"/> below was not an input before this. Two
/// files in two different folders that happened to share a name, a byte length and a modification
/// time hashed identically — same <c>mediaVersion</c>, same <c>ETag</c>, same entry in the
/// server's own disk cache (<c>MediaEndpoints.cs</c>'s cache key is exactly
/// <see cref="EntityHex"/>) — so the server could serve one photograph's pixels under another
/// photograph's name, and because responses are <c>Cache-Control: private, max-age=31536000,
/// immutable</c>, a phone would hold the wrong picture for a year. Folding the folder in fixes
/// that.
/// <para/>
/// <b>Side effect worth knowing about.</b> Moving or renaming a folder on disk changes this input,
/// so it changes every ETag under it in one stroke. That is not a bug — every client simply
/// revalidates once, gets a fresh ETag, and the bytes it receives were always correct — but it is a
/// real, visible, one-time cost after a move or rename that is worth writing down where an operator
/// will read it.
/// </summary>
public readonly struct MediaFingerprint
{
    private readonly byte[] _hash;

    private MediaFingerprint(byte[] hash) => _hash = hash;

    /// <summary>
    /// <paramref name="id"/> MUST be the on-disk spelling, not the spelling the client sent
    /// (§ 11.1.6). On a case-insensitive filesystem the two can differ, and an ETag that varied
    /// with the caller's casing would not be the same entity tag for the same bytes.
    /// <para/>
    /// <paramref name="folder"/> MUST likewise be the canonical, absolute on-disk folder — the
    /// open session's own folder (already resolved with <see cref="Path.GetFullPath(string)"/> by
    /// the caller, § 11.2 step 5), never a client-supplied or relative spelling. It is normalised
    /// again here (full path, trailing separator trimmed) so that the identical physical directory
    /// hashes identically no matter which of this type's callers supplied it — the session folder
    /// directly, or a <see cref="FileInfo"/> built from <c>folder + id</c>. This is the one new
    /// input that makes two files agreeing on name, size and mtime in two different folders hash
    /// differently (second audit, § 1.2).
    /// </summary>
    public static MediaFingerprint Of(string id, long sizeBytes, long mtimeUtcTicks, string folder)
    {
        var folderBytes = Encoding.UTF8.GetBytes(NormalizeFolder(folder));
        var idBytes = Encoding.UTF8.GetBytes(id);
        var sizeBytesText = Encoding.UTF8.GetBytes(sizeBytes.ToString(CultureInfo.InvariantCulture));
        var ticksText = Encoding.UTF8.GetBytes(mtimeUtcTicks.ToString(CultureInfo.InvariantCulture));

        var buffer = new byte[folderBytes.Length + 1 + idBytes.Length + 1 + sizeBytesText.Length + 1 + ticksText.Length];
        var at = 0;
        folderBytes.CopyTo(buffer, at);
        at += folderBytes.Length;
        buffer[at++] = 0x00;
        idBytes.CopyTo(buffer, at);
        at += idBytes.Length;
        buffer[at++] = 0x00;
        sizeBytesText.CopyTo(buffer, at);
        at += sizeBytesText.Length;
        buffer[at++] = 0x00;
        ticksText.CopyTo(buffer, at);

        return new MediaFingerprint(SHA256.HashData(buffer));
    }

    /// <summary>
    /// <paramref name="file"/> already carries its folder — <see cref="FileInfo.DirectoryName"/> —
    /// so every caller of this overload (notably <c>SessionRegistry.FingerprintOf</c>, which builds
    /// <paramref name="file"/> from <c>Path.Combine(folder, id)</c>) gets the second audit's § 1.2
    /// fix without having to plumb a separate folder argument through.
    /// </summary>
    public static MediaFingerprint Of(string id, FileInfo file) =>
        Of(id, file.Length, File.GetLastWriteTimeUtc(file.FullName).Ticks, file.DirectoryName ?? "");

    /// <summary>
    /// Full path, trailing directory separator trimmed. Not a case-fold: like <see cref="Of"/>'s
    /// <c>id</c>, the folder is required to already be the on-disk spelling, so two different
    /// callers computing the same physical directory (a session's own <c>Folder</c>, or
    /// <see cref="FileInfo.DirectoryName"/> off a path built from that same folder) always land on
    /// the same bytes here.
    /// </summary>
    private static string NormalizeFolder(string folder)
    {
        var full = Path.GetFullPath(string.IsNullOrEmpty(folder) ? "." : folder);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

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
