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
        var db = TryLoad(Path.Combine(folder, FileName));
        var policy = Classify(onDisk.Select(x => x.Kind));

        var result = new List<MediaRecord>();
        foreach (var (id, kind) in onDisk)
        {
            if (policy == MediaKind.Still && kind != MediaKind.Still)
                continue;

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
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var tmp = path + ".tmp";

        var dto = new RankingDatabaseDto
        {
            Version = 1,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Images = records.ToDictionary(
                r => r.Filename,
                r => new ImageRecordDto
                {
                    Filename = r.Filename,
                    Rating = new RatingDto { Mu = r.Rating.Mu, Sigma = r.Rating.Sigma },
                    Matches = r.Matches,
                    Impressions = r.Impressions,
                    LastPlayed = r.LastPlayed
                },
                StringComparer.OrdinalIgnoreCase)
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

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

    public static MediaKind Classify(IEnumerable<MediaKind> kinds)
    {
        var sawStill = false;
        var sawVideo = false;
        foreach (var k in kinds)
        {
            if (k == MediaKind.Still) sawStill = true;
            else sawVideo = true;
        }

        if (sawVideo && !sawStill)
            return MediaKind.Video;
        return MediaKind.Still;
    }

    internal static RankingDatabaseDto TryLoad(string path)
    {
        if (!File.Exists(path))
            return new RankingDatabaseDto();

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RankingDatabaseDto>(json, JsonOptions);
            return dto ?? new RankingDatabaseDto();
        }
        catch (JsonException)
        {
            return new RankingDatabaseDto();
        }
    }

    private static List<(MediaId Id, MediaKind Kind)> ListTopLevelMedia(string folder)
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
