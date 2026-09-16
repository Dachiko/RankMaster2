using System.Text.Json;
using RankMaster2.Ranking;

namespace RankMaster2.Catalog;

public sealed class JsonCatalog : ICatalog
{
    public const string FileName = "rankmaster_db.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<MediaRecord> Scan(string folder)
    {
        var onDisk = ListTopLevelMedia(folder);
        var db = LoadRequired(Path.Combine(folder, FileName));

        var result = new List<MediaRecord>();
        foreach (var (id, kind) in onDisk)
        {
            if (db.Images.TryGetValue(id.Filename, out var row))
            {
                result.Add(new MediaRecord(
                    id,
                    kind,
                    new Rating(row.Rating.Mu, row.Rating.Sigma),
                    row.Matches,
                    row.Impressions,
                    row.LastPlayed));
            }
            else
            {
                result.Add(new MediaRecord(
                    id,
                    kind,
                    RankingConstants.DefaultRating,
                    Matches: 0,
                    Impressions: 0,
                    LastPlayed: 0));
            }
        }

        return result;
    }

    public void Save(string folder, IReadOnlyList<MediaRecord> records)
    {
        // Never create the folder here. If it has gone - USB pulled, share dropped, renamed in
        // Explorer while the app is open - recreating it makes the write succeed against an empty
        // directory, and the merge below then installs an empty database over real ratings. It has
        // to throw: that is what lets RankingSession roll the choice back instead of losing it.
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Ranking folder is gone; refusing to save: " + folder);

        var path = Path.Combine(folder, FileName);
        var onDisk = ListTopLevelMedia(folder);
        var existing = File.Exists(path) ? LoadRequired(path) : new RankingDatabaseDto();
        var session = records.ToDictionary(r => r.Filename, StringComparer.OrdinalIgnoreCase);

        var images = new Dictionary<string, ImageRecordDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, kind) in onDisk)
        {
            if (session.TryGetValue(id.Filename, out var rec))
            {
                images[id.Filename] = ToDto(rec);
                continue;
            }

            if (existing.Images.TryGetValue(id.Filename, out var row))
            {
                images[id.Filename] = row;
                continue;
            }

            images[id.Filename] = ToDto(new MediaRecord(
                id, kind, RankingConstants.DefaultRating, 0, 0, 0));
        }

        // The session is holding records but the folder lists no media at all. That is not
        // "everything was discarded" - discarded files leave the record list too. It means the
        // library became unreadable between the scan and now. Never trade real ratings for {}.
        if (images.Count == 0 && records.Count > 0)
            throw new IOException(
                $"Folder lists no media but the session holds {records.Count} record(s); " +
                $"refusing to overwrite {FileName} in {folder}.");

        var dto = new RankingDatabaseDto
        {
            Version = 1,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Images = images
        };

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    private static ImageRecordDto ToDto(MediaRecord r) => new()
    {
        Filename = r.Filename,
        Rating = new RatingDto { Mu = r.Rating.Mu, Sigma = r.Rating.Sigma },
        Matches = r.Matches,
        Impressions = r.Impressions,
        LastPlayed = r.LastPlayed
    };

    public IReadOnlyList<MediaRecord> RemapIds(
        IReadOnlyList<MediaRecord> records,
        IReadOnlyDictionary<MediaId, MediaId> map)
    {
        return records.Select(r =>
        {
            if (!map.TryGetValue(r.Id, out var next))
                return r;
            return r with { Id = next };
        }).ToList();
    }

    public static MediaKind Classify(IEnumerable<MediaKind> kinds) =>
        MediaExtensions.RankPolicy(kinds);

    internal static RankingDatabaseDto LoadRequired(string path)
    {
        if (!File.Exists(path))
            return new RankingDatabaseDto();

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RankingDatabaseDto>(json, JsonOptions);
            if (dto is null)
                throw new InvalidDataException("Ranking file is empty: " + path);
            dto.Images = new Dictionary<string, ImageRecordDto>(
                dto.Images, StringComparer.OrdinalIgnoreCase);
            return dto;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Ranking file is unreadable and was not overwritten: " + path, ex);
        }
    }

    /// <summary>
    /// Internal rather than private so <see cref="RenameEngine"/> can list the folder's media files
    /// the identical way <see cref="Scan"/> and <see cref="Save"/> do — recovery and cancel's
    /// "reunite" step (SERVER_SPEC.md § 10.16) must see exactly the same set, or a file one of them
    /// skips is a rating the other silently drops.
    /// </summary>
    internal static List<(MediaId Id, MediaKind Kind)> ListTopLevelMedia(string folder)
    {
        var list = new List<(MediaId, MediaKind)>();
        if (!Directory.Exists(folder))
            return list;

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (name.Equals(FileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0)
                continue;

            var kind = MediaExtensions.KindOf(name);
            if (kind is null)
                continue;

            list.Add((new MediaId(name), kind.Value));
        }

        return list;
    }
}
