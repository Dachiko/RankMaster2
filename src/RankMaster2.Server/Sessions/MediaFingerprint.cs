using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// SERVER_SPEC.md § 12.2, the half of it a snapshot needs: <c>mediaVersion</c>. The ETag half
/// belongs to the media endpoints and is not built here.
/// </summary>
internal static class MediaFingerprint
{
    /// <summary>
    /// <c>SHA-256(utf8(id) | 0x00 | sizeBytes | 0x00 | mtimeUtcTicks)</c>, first 8 bytes as 16
    /// lowercase hex characters. Null when the file could not be stat'd — it has been moved or
    /// deleted under the session (§ 7.4.8), and <c>sizeBytes</c> is then null too (§ 9.3).
    /// </summary>
    public static (long? SizeBytes, string? MediaVersion) Of(string folder, string id)
    {
        try
        {
            var info = new FileInfo(Path.Combine(folder, id));
            if (!info.Exists)
                return (null, null);

            var size = info.Length;
            var ticks = File.GetLastWriteTimeUtc(info.FullName).Ticks;

            var message = new MemoryStream();
            Append(message, id);
            message.WriteByte(0x00);
            Append(message, size.ToString(CultureInfo.InvariantCulture));
            message.WriteByte(0x00);
            Append(message, ticks.ToString(CultureInfo.InvariantCulture));

            var hash = SHA256.HashData(message.ToArray());
            return (size, Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static void Append(Stream to, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        to.Write(bytes, 0, bytes.Length);
    }
}
