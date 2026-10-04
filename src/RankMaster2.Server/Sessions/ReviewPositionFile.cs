using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// SERVER_SPEC.md § 10.17 / § 10.19: where the phone's review mode left off, stored in the library
/// folder so it follows the folder and not the phone. <c>&lt;folder&gt;/.rankmaster_review.json</c>,
/// <c>{ "version": 1, "current": "&lt;media id&gt;", "updatedAt": "&lt;RFC 3339 UTC&gt;" }</c>.
/// <para/>
/// Bookkeeping, like <c>.rankmaster.lock</c> and <c>.rankmaster-rename.json</c>: its extension is not
/// a media extension (so <c>JsonCatalog.Scan</c> and the rename engine never list it), it is Hidden on
/// Windows, and it is NOT part of <c>rankmaster_db.json</c>, whose format is frozen. A bad file is
/// never an error: it reads as "no position".
/// </summary>
internal static class ReviewPositionFile
{
    public const string FileName = ".rankmaster_review.json";

    /// <summary>The file is a few dozen bytes; anything bigger is not ours and is not read.</summary>
    private const int MaxBytes = 4096;

    private const int KnownVersion = 1;

    public static string PathOf(string folder) => Path.Combine(folder, FileName);

    /// <summary>The stored id as written, or null: no file, unreadable, malformed, or an unknown version.</summary>
    public static string? Read(string folder, ILogger? log)
    {
        var path = PathOf(folder);
        try
        {
            if (!File.Exists(path))
                return null;

            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                log?.LogWarning("Ignoring {Path}: larger than {Max} bytes.", path, MaxBytes);
                return null;
            }

            // A hand edit in Notepad may add a UTF-8 BOM, which the JSON parser rejects.
            var bytes = File.ReadAllBytes(path);
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(start));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) ||
                number != KnownVersion)
            {
                log?.LogWarning("Ignoring {Path}: not a version {Version} review position.", path, KnownVersion);
                return null;
            }

            if (!root.TryGetProperty("current", out var current) ||
                current.ValueKind != JsonValueKind.String ||
                current.GetString() is not { Length: > 0 } id ||
                !Media.MediaIdCodec.Validate(id).Ok)
            {
                log?.LogWarning("Ignoring {Path}: 'current' is missing or not a legal media id.", path);
                return null;
            }

            return id;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            log?.LogWarning(e, "Ignoring {Path}: it could not be read.", path);
            return null;
        }
    }

    /// <summary>
    /// Temp file in the same folder, fsynced, then replaced over the real one, so a crash leaves the
    /// old position or the new one, never half of either. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>; the caller turns that into <c>save_failed</c>.
    /// </summary>
    public static void Write(string folder, string id, DateTimeOffset now)
    {
        var path = PathOf(folder);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = KnownVersion,
            ["current"] = id,
            ["updatedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        });

        // A temp file left hidden by a crash would make FileMode.Create throw on Windows (it refuses
        // to truncate a hidden file), wedging every later write. Clear it first.
        if (File.Exists(tmp))
        {
            File.SetAttributes(tmp, FileAttributes.Normal);
            File.Delete(tmp);
        }

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        // Hidden before the swap, so on Windows the replace is hidden-over-hidden: replacing a hidden
        // file with a visible one is what some volumes refuse.
        Hide(tmp);

        try
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && File.Exists(path))
            {
                // A volume that cannot File.Replace (some network shares): fall back to delete + move.
                // Both are directory-entry operations and the content is already safe under the temp name.
                File.Delete(path);
                File.Move(tmp, path);
            }
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        Hide(path);
    }

    /// <summary>Removes the file; a missing file is success. Throws on a real failure.</summary>
    public static void Delete(string folder)
    {
        var path = PathOf(folder);
        File.Delete(path);
        try
        {
            File.Delete(path + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Cosmetic and best effort, never a reason to fail (the same stance as <c>FolderLock</c>).</summary>
    private static void Hide(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
