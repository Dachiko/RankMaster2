using System.Text;
using RankMaster2.Audit.Compatibility.Support;
using RankMaster2.Catalog;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Compatibility;

/// <summary>
/// SPEC.md § Persistence: "Must load existing v1 files from Rank Master 1. Do not invent a new
/// schema." The owner's libraries are years old and the desktop app has to keep opening them, so
/// the shape of the bytes is the contract, not an implementation detail.
/// </summary>
public sealed class V1SchemaTests
{
    /// <summary>
    /// The library behind <c>Golden/rankmaster_db.v1.json</c>. Chosen so that every part of the
    /// encoding is pinned by something: a default rating, a long fraction, a whole-number sigma, a
    /// negative mu, a space in a name, an uppercase extension, a video, and a name that is
    /// Latin-1, Cyrillic, an em dash and a numero sign at once.
    /// </summary>
    private static readonly (string Name, double Mu, double Sigma, int Matches, int Impressions, long LastPlayed)[]
        GoldenLibrary =
        [
            ("alpha.jpg", 25.0, 8.333, 0, 0, 0),
            ("bravo.png", 31.415926535897931, 4.25, 12, 19, 1700000000000L),
            ("charlie clip.mp4", 18.5, 6.0, 3, 5, 1234567890123L),
            ("delta.JPG", -0.5, 0.001, 999, 1000, 1),
            ("Ärger am Fluß — фото №7.jpg", 25.0, 8.333, 1, 2, 42),
        ];

    private static string GoldenPath =>
        Path.Combine(Repo.Root, "tests", "RankMaster2.Audit.Compatibility", "Golden", "rankmaster_db.v1.json");

    [Fact]
    public void The_v1_schema_is_byte_for_byte_unchanged()
    {
        using var folder = Scratch.New("golden");
        var records = new List<MediaRecord>();
        foreach (var (name, mu, sigma, matches, impressions, lastPlayed) in GoldenLibrary)
        {
            folder.Write(name, [1, 2, 3]);
            records.Add(new MediaRecord(
                new MediaId(name), MediaExtensions.KindOf(name)!.Value,
                new Rating(mu, sigma), matches, impressions, lastPlayed));
        }

        new JsonCatalog().Save(folder.Path, records);

        var actual = Db.Canonicalise(Db.ReadText(folder.Path));
        var expected = File.ReadAllText(GoldenPath, Encoding.UTF8).ReplaceLineEndings("\n");

        Assert.True(expected == actual, Diff(expected, actual));
    }

    [Fact]
    public void A_saved_file_has_exactly_the_v1_fields()
    {
        using var folder = Scratch.New("shape");
        folder.Stills(4);
        var catalog = new JsonCatalog();
        catalog.Save(folder.Path, catalog.Scan(folder.Path));

        Db.RequireV1Schema(folder.Path, "JsonCatalog.Save writes v1 (SPEC.md § Persistence)");
    }

    [Fact]
    public void Version_is_one_even_when_the_file_on_disk_claimed_otherwise()
    {
        using var folder = Scratch.New("version");
        folder.Stills(3);
        File.WriteAllText(Db.PathIn(folder.Path), """
            {"version": 7, "lastUpdated": 1, "images": {}}
            """);

        var catalog = new JsonCatalog();
        catalog.Save(folder.Path, catalog.Scan(folder.Path));

        Assert.Equal(1, Db.Read(folder.Path).GetProperty("version").GetInt32());
    }

    /// <summary>
    /// The one field the server is allowed to move on its own. SPEC.md shows it in the schema and
    /// nothing reads it, but a v1 file without it is not a v1 file.
    /// </summary>
    [Fact]
    public void LastUpdated_advances_on_every_save()
    {
        using var folder = Scratch.New("stamp");
        folder.Stills(3);
        var catalog = new JsonCatalog();
        var records = catalog.Scan(folder.Path);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        catalog.Save(folder.Path, records);
        var stamp = Db.Read(folder.Path).GetProperty("lastUpdated").GetInt64();
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.InRange(stamp, before, after);
    }

    /// <summary>
    /// SPEC.md § Persistence: "μ − 3σ is computed, never stored." A stored conservative score
    /// would be a new field, and it would go stale the moment Rank Master 1 wrote the file.
    /// </summary>
    [Fact]
    public void The_conservative_score_is_never_stored()
    {
        using var folder = Scratch.New("score");
        folder.Stills(3);
        var catalog = new JsonCatalog();
        catalog.Save(folder.Path, catalog.Scan(folder.Path));

        var text = Db.ReadText(folder.Path);
        foreach (var forbidden in new[] { "conservative", "score", "rank", "mu3", "displayRating" })
        {
            Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"The saved file mentions '{forbidden}'. SPEC.md § Persistence stores mu and sigma and " +
                $"nothing derived from them.\n{text}");
        }
    }

    private static string Diff(string expected, string actual)
    {
        var left = expected.Split('\n');
        var right = actual.Split('\n');
        var report = new StringBuilder(
            "The bytes of rankmaster_db.json changed. SPEC.md § Persistence: \"Do not invent a new schema.\"\n" +
            "The desktop app and Rank Master 1 read this file; a change here is a change to every library the\n" +
            "owner has. Golden file: " + GoldenPath + "\n\n");

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : "(no line)";
            var b = i < right.Length ? right[i] : "(no line)";
            if (a == b) continue;
            report.Append($"  line {i + 1}:\n    golden: {a}\n    actual: {b}\n");
        }

        return report.ToString();
    }
}
