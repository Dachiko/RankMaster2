using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Catalog.Tests;

public class JsonCatalogTests
{
    [Fact]
    public void Classify_MixedFolder_IsStillsOnly()
    {
        Assert.Equal(MediaKind.Still, JsonCatalog.Classify([MediaKind.Still, MediaKind.Video]));
        Assert.Equal(MediaKind.Video, JsonCatalog.Classify([MediaKind.Video]));
        Assert.Equal(MediaKind.Still, JsonCatalog.Classify([MediaKind.Still]));
    }

    [Fact]
    public void SaveAndScan_RoundTripsRatings()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "b.jpg"), [0]);

        var catalog = new JsonCatalog();
        var first = catalog.Scan(dir);
        Assert.Equal(2, first.Count);

        var updated = first.Select((r, i) => i == 0
            ? r with { Rating = new Rating(30, 4), Matches = 3, Impressions = 5 }
            : r).ToList();
        catalog.Save(dir, updated);

        var again = catalog.Scan(dir);
        var a = again.Single(r => r.Filename == "a.jpg");
        Assert.Equal(30, a.Rating.Mu);
        Assert.Equal(4, a.Rating.Sigma);
        Assert.Equal(3, a.Matches);
        Assert.Equal(5, a.Impressions);
    }

    [Fact]
    public void Scan_MergesNewFiles_AndDropsMissing()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "keep.jpg"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "gone.jpg"), [0]);

        var catalog = new JsonCatalog();
        catalog.Save(dir, catalog.Scan(dir));
        File.Delete(Path.Combine(dir, "gone.jpg"));
        File.WriteAllBytes(Path.Combine(dir, "new.jpg"), [0]);

        var records = catalog.Scan(dir);
        Assert.DoesNotContain(records, r => r.Filename == "gone.jpg");
        var added = records.Single(r => r.Filename == "new.jpg");
        Assert.Equal(RankingConstants.InitialMu, added.Rating.Mu);
        Assert.Equal(0, added.Matches);
    }

    [Fact]
    public void Scan_IgnoresSubfoldersAndJsonFile()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "top.jpg"), [0]);
        Directory.CreateDirectory(Path.Combine(dir, "discarded"));
        File.WriteAllBytes(Path.Combine(dir, "discarded", "nope.jpg"), [0]);
        File.WriteAllText(Path.Combine(dir, JsonCatalog.FileName), "{}");

        var records = new JsonCatalog().Scan(dir);
        Assert.Single(records);
        Assert.Equal("top.jpg", records[0].Filename);
    }

    [Fact]
    public void Scan_MixedFolder_ReturnsAllFiles_PolicyIsStills()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "pic.jpg"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "clip.mp4"), [0]);

        var records = new JsonCatalog().Scan(dir);
        Assert.Equal(2, records.Count);
        Assert.Equal(MediaKind.Still, JsonCatalog.Classify(records.Select(r => r.Kind)));
    }

    [Fact]
    public void Save_MixedFolder_KeepsVideoRows()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "pic.jpg"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "clip.mp4"), [0]);
        var catalog = new JsonCatalog();
        catalog.Save(dir, catalog.Scan(dir));

        var onlyStill = catalog.Scan(dir).Where(r => r.Kind == MediaKind.Still).ToList();
        onlyStill[0] = onlyStill[0] with { Matches = 3, Rating = new Rating(30, 4) };
        catalog.Save(dir, onlyStill);

        var again = catalog.Scan(dir);
        Assert.Contains(again, r => r.Filename == "clip.mp4");
        Assert.Equal(3, again.Single(r => r.Filename == "pic.jpg").Matches);
    }

    [Fact]
    public void Scan_MatchesJsonKeysIgnoringCase()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "Photo.JPG"), [0]);
        File.WriteAllText(Path.Combine(dir, JsonCatalog.FileName), """
            {
              "version": 1,
              "lastUpdated": 1,
              "images": {
                "photo.jpg": {
                  "filename": "photo.jpg",
                  "rating": { "mu": 30, "sigma": 2 },
                  "matches": 4,
                  "impressions": 4,
                  "lastPlayed": 1
                }
              }
            }
            """);

        var rec = new JsonCatalog().Scan(dir).Single();
        Assert.Equal("Photo.JPG", rec.Filename);
        Assert.Equal(30, rec.Rating.Mu);
        Assert.Equal(4, rec.Matches);
    }

    [Fact]
    public void Scan_CorruptJson_DoesNotOverwrite()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [0]);
        var db = Path.Combine(dir, JsonCatalog.FileName);
        File.WriteAllText(db, "{not-json");
        var before = File.ReadAllText(db);
        Assert.Throws<InvalidDataException>(() => new JsonCatalog().Scan(dir));
        Assert.Equal(before, File.ReadAllText(db));
    }

    [Fact]
    public void MissingImpressions_DefaultToZero()
    {
        var dir = CreateTempFolder();
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [0]);
        File.WriteAllText(Path.Combine(dir, JsonCatalog.FileName), """
            {
              "version": 1,
              "lastUpdated": 1,
              "images": {
                "a.jpg": {
                  "filename": "a.jpg",
                  "rating": { "mu": 26.5, "sigma": 5.0 },
                  "matches": 2,
                  "lastPlayed": 9
                }
              }
            }
            """);

        var a = new JsonCatalog().Scan(dir).Single();
        Assert.Equal(0, a.Impressions);
        Assert.Equal(26.5, a.Rating.Mu);
        Assert.Equal(2, a.Matches);
    }

    [Fact]
    public void RemapIds_RewritesFilenameIdentity()
    {
        var catalog = new JsonCatalog();
        var rec = new MediaRecord(new MediaId("old.jpg"), MediaKind.Still, RankingConstants.DefaultRating, 1, 1, 1);
        var mapped = catalog.RemapIds([rec], new Dictionary<MediaId, MediaId>
        {
            [rec.Id] = new MediaId("000001.jpg")
        });
        Assert.Equal("000001.jpg", mapped[0].Filename);
        Assert.Equal(1, mapped[0].Matches);
    }

    private static string CreateTempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
