using System.Globalization;
using System.Security.Cryptography;
using RankMaster2.Audit.Compatibility.Support;
using RankMaster2.Catalog;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Compatibility;

/// <summary>
/// The whole audit in one sentence: a file the server wrote must load back through
/// <c>JsonCatalog.Scan</c> with every rating, match count, impression count and timestamp intact.
/// <c>Scan</c> is what the desktop app calls, so these tests are the desktop app opening the
/// folder after the phone has been ranking in it.
/// </summary>
public sealed class RoundTripTests(Rm2Server server) : AuditSessionTest(server)
{
    [Fact]
    public async Task Every_field_the_server_wrote_loads_back_unchanged()
    {
        using var folder = Scratch.New("roundtrip");
        folder.Stills(8);

        var client = await ClientAsync();
        var snapshot = await OpenAsync(folder.Path);

        var voted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 12 && snapshot.IsRanking; i++)
        {
            var token = snapshot.RequireToken("a ranking session has a pairToken");
            voted.Add(snapshot.Left.Id);
            voted.Add(snapshot.Right.Id);
            snapshot = (await client.VoteAsync(token, i % 2 == 0 ? "left" : "right"))
                .ShouldBeSnapshot(200, "POST /session/vote applies and saves (SERVER_SPEC.md § 10.6)");
        }

        await CloseAsync();

        // The file on disk, read the way the desktop app reads it.
        var loaded = new JsonCatalog().Scan(folder.Path).ToDictionary(r => r.Filename, StringComparer.Ordinal);
        var raw = Db.Rows(folder.Path);

        Db.RequireV1Schema(folder.Path, "the file the server wrote is v1");
        Assert.Equal(8, loaded.Count);

        foreach (var (id, record) in loaded)
        {
            var row = raw[id];
            Assert.Equal(row.Mu, record.Rating.Mu);
            Assert.Equal(row.Sigma, record.Rating.Sigma);
            Assert.Equal(row.Matches, record.Matches);
            Assert.Equal(row.Impressions, record.Impressions);
            Assert.Equal(row.LastPlayed, record.LastPlayed);
        }

        foreach (var id in voted)
        {
            var record = loaded[id];
            Assert.True(record.Matches > 0,
                $"'{id}' was voted on but came back with matches = 0. SERVER_SPEC.md § 10.6 sets " +
                "matches + 1 on both records inside the same call that saves.");
            Assert.True(record.Impressions > 0, $"'{id}' was voted on but came back with impressions = 0.");
            Assert.True(record.LastPlayed > 0, $"'{id}' was voted on but came back with lastPlayed = 0.");
            Assert.True(record.Rating.Sigma < RankMaster2.Ranking.RankingConstants.InitialSigma,
                $"'{id}' was voted on but its sigma is still the initial {record.Rating.Sigma:R}; " +
                "the rating did not move.");
        }
    }

    /// <summary>
    /// Reopening is the desktop app's Resume. Nothing may drift on the way through.
    /// </summary>
    [Fact]
    public async Task Reopening_and_saving_again_changes_nothing_but_the_timestamp()
    {
        using var folder = Scratch.New("resume");
        folder.Stills(6);

        var client = await ClientAsync();
        var snapshot = await OpenAsync(folder.Path);
        for (var i = 0; i < 6 && snapshot.IsRanking; i++)
        {
            snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, "vote");
        }
        await CloseAsync();

        var before = Db.Rows(folder.Path);

        await OpenAsync(folder.Path);
        (await client.SaveAsync()).ShouldBeSnapshot(200, "POST /session/save is idempotent (SERVER_SPEC.md § 10.5)");
        await CloseAsync();

        var after = Db.Rows(folder.Path);

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, row) in before)
        {
            Assert.True(row == after[key],
                $"Reopening the folder and saving changed '{key}'.\n  before: {row}\n  after:  {after[key]}");
        }
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.7: skip moves impressions and nothing else. The desktop app's
    /// leaderboard is built from `matches`, so a skip that bumped it would silently reorder a
    /// library.
    /// </summary>
    [Fact]
    public async Task Skip_moves_impressions_and_leaves_the_rating_alone()
    {
        using var folder = Scratch.New("skip");
        folder.Stills(4);

        var client = await ClientAsync();
        var snapshot = await OpenAsync(folder.Path);
        var left = snapshot.Left.Id;
        var right = snapshot.Right.Id;

        (await client.SkipAsync(snapshot.RequireToken("ranking")))
            .ShouldBeSnapshot(200, "POST /session/skip (SERVER_SPEC.md § 10.7)");
        await CloseAsync();

        var rows = Db.Rows(folder.Path);
        foreach (var id in new[] { left, right })
        {
            Assert.Equal(1, rows[id].Impressions);
            Assert.Equal(0, rows[id].Matches);
            Assert.Equal(0, rows[id].LastPlayed);
            Assert.Equal(RankMaster2.Ranking.RankingConstants.InitialMu, rows[id].Mu);
            Assert.Equal(RankMaster2.Ranking.RankingConstants.InitialSigma, rows[id].Sigma);
        }
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.16, the compatibility half of the acceptance gate: a file the server
    /// renamed must still be exactly what the desktop app (and Rank Master 1) would open — v1
    /// schema, every key equal to its own `filename` field, every rating intact under the new
    /// `000001-<suffix>.ext …` names — which must still sort by rank, since sorted order is the
    /// entire reason the owner renames.
    /// </summary>
    [Fact]
    public async Task A_renamed_library_still_loads_in_the_desktop_app()
    {
        using var folder = Scratch.New("rename-roundtrip");
        folder.Stills(6);

        var client = await ClientAsync();
        var snapshot = await OpenAsync(folder.Path);

        var voted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 8 && snapshot.IsRanking; i++)
        {
            voted.Add(snapshot.Left.Id);
            voted.Add(snapshot.Right.Id);
            snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), i % 2 == 0 ? "left" : "right"))
                .ShouldBeSnapshot(200, "POST /session/vote (SERVER_SPEC.md § 10.6)");
        }

        // SERVER_SPEC.md § 13.1 / § 10.5: a vote's 200 means applied, not yet on disk. This test
        // compares the file before the rename with the file after it, so it takes the contract's own
        // durability point first — the 200 from POST /session/save is the strongest statement the
        // server makes, and it is what any reader of rankmaster_db.json is told to ask for.
        (await client.SaveAsync()).ShouldBeSnapshot(200, "POST /session/save (SERVER_SPEC.md § 10.5)");

        var beforeRows = Db.Rows(folder.Path);

        var start = await client.StartRenameAsync();
        start.ShouldHaveStatus(202, "POST /session/rename (SERVER_SPEC.md § 10.16)");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        Rm2Response poll;
        string? state;
        while (true)
        {
            poll = await client.GetRenameAsync();
            poll.ShouldHaveStatus(200, "GET /session/rename while polling");
            state = poll.JsonBody.GetProperty("state").GetString();
            if (state is "succeeded" or "cancelled" or "failed")
                break;
            if (DateTime.UtcNow > deadline)
                throw poll.Failure("the rename did not reach a terminal state within 30s");
            await Task.Delay(20);
        }

        Assert.Equal("succeeded", state);

        await CloseAsync();

        // The desktop app's own read path: JsonCatalog.Scan, never the server's DTOs.
        var loaded = new JsonCatalog().Scan(folder.Path).ToDictionary(r => r.Filename, StringComparer.Ordinal);
        Assert.Equal(6, loaded.Count);
        Assert.All(loaded.Keys, key => Assert.Matches("^\\d{6}-[0-9a-f]{4}\\.", key));

        // One suffix for the whole run, so sorting the folder by name is sorting it by rank.
        Assert.Single(loaded.Keys.Select(k => k.Split('-')[1]).Distinct());
        Assert.Equal(
            loaded.Keys.OrderBy(k => k, StringComparer.Ordinal),
            Enumerable.Range(1, 6).Select(i => loaded.Keys.Single(k => k.StartsWith($"{i:D6}-", StringComparison.Ordinal))));

        Db.RequireV1Schema(folder.Path, "a renamed library is still v1 (SPEC.md § Persistence)");

        var rows = Db.Rows(folder.Path);
        Assert.Equal(6, rows.Count);
        foreach (var (key, row) in rows)
            Assert.Equal(key, row.Filename);   // the key and the `filename` field always agree

        // Every rating the server produced before the rename is still there under a new name — the
        // conservative-score ordering means we cannot predict which new name went to which old one
        // without recomputing it, so match by the (mu, sigma, matches, impressions, lastPlayed)
        // tuple, which the rename carries verbatim (it is not recomputed).
        var beforeTuples = beforeRows.Values
            .Select(r => (r.Mu, r.Sigma, r.Matches, r.Impressions, r.LastPlayed))
            .OrderBy(t => t)
            .ToList();
        var afterTuples = rows.Values
            .Select(r => (r.Mu, r.Sigma, r.Matches, r.Impressions, r.LastPlayed))
            .OrderBy(t => t)
            .ToList();
        Assert.Equal(beforeTuples, afterTuples);

        foreach (var id in voted)
            Assert.DoesNotContain(id, rows.Keys);   // the old names are gone; the ratings are not
    }

    /// <summary>
    /// The owner's own folder, from G-audit-remediation § 0.2: Rank Master 2's rename produces
    /// exactly <c>000001.jpg …</c>, so his folders very likely hold those names already. This proves
    /// they are ordinary input — the server renames them in one pass, every rating stays on the same
    /// bytes, and the file it leaves behind is still a v1 database the frozen desktop app opens
    /// (§ 0.2's hard constraint: the schema does not change).
    /// </summary>
    [Fact]
    public async Task A_folder_already_named_by_Rank_Master_2_renames_cleanly()
    {
        using var folder = Scratch.New("rm2-names");

        // The folder as Rank Master 2 left it: six files named by rank, and a v1 database keyed by
        // those names, with ratings that put them in a different order than their names suggest.
        var seeded = new (string Name, double Mu, double Sigma, int Matches, int Impressions, long LastPlayed)[]
        {
            ("000001.jpg", 31.5, 3.10, 21, 30, 1_700_000_000_000),
            ("000002.jpg", 28.25, 3.55, 18, 26, 1_700_000_000_001),
            ("000003.jpg", 26.00, 4.00, 15, 22, 1_700_000_000_002),
            ("000004.jpg", 24.75, 4.45, 12, 18, 1_700_000_000_003),
            ("000005.jpg", 22.50, 4.90, 9, 14, 1_700_000_000_004),
            ("000006.jpg", 19.25, 5.35, 6, 10, 1_700_000_000_005),
        };

        for (var i = 0; i < seeded.Length; i++)
            folder.Jpeg(seeded[i].Name, 200 + (i * 11), 150 + (i * 7));   // distinct bytes per picture

        var images = string.Join(",\n", seeded.Select(s => $$"""
                "{{s.Name}}": {
                  "filename": "{{s.Name}}",
                  "rating": { "mu": {{s.Mu.ToString(CultureInfo.InvariantCulture)}}, "sigma": {{s.Sigma.ToString(CultureInfo.InvariantCulture)}} },
                  "matches": {{s.Matches}},
                  "impressions": {{s.Impressions}},
                  "lastPlayed": {{s.LastPlayed}}
                }
            """));
        folder.WriteText(Db.FileName, "{\n  \"version\": 1,\n  \"lastUpdated\": 1700000000000,\n  \"images\": {\n" + images + "\n  }\n}\n");
        Db.RequireV1Schema(folder.Path, "the folder Rank Master 2 left behind is a v1 database");

        var client = await ClientAsync();
        var snapshot = await OpenAsync(folder.Path);

        for (var i = 0; i < 4 && snapshot.IsRanking; i++)
            snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), i % 2 == 0 ? "left" : "right"))
                .ShouldBeSnapshot(200, "POST /session/vote (SERVER_SPEC.md § 10.6)");

        // SERVER_SPEC.md § 13.1 / § 10.5: the votes above are applied and on disk within the bound;
        // reading the file is what makes this the moment to ask for the durability point.
        (await client.SaveAsync()).ShouldBeSnapshot(200, "POST /session/save (SERVER_SPEC.md § 10.5)");

        // Which rating sits on which picture, keyed by the picture's own bytes — the only identity a
        // rename cannot change.
        var before = RatingByBytes(folder.Path);
        Assert.Equal(6, before.Count);

        (await client.StartRenameAsync()).ShouldHaveStatus(202, "POST /session/rename (SERVER_SPEC.md § 10.16)");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? state;
        while (true)
        {
            var poll = await client.GetRenameAsync();
            poll.ShouldHaveStatus(200, "GET /session/rename while polling");
            state = poll.JsonBody.GetProperty("state").GetString();
            if (state is "succeeded" or "cancelled" or "failed")
                break;
            if (DateTime.UtcNow > deadline)
                throw poll.Failure("the rename did not reach a terminal state within 30s");
            await Task.Delay(20);
        }

        Assert.Equal("succeeded", state);
        await CloseAsync();

        // Every name is a new name of one run: no 000001.jpg survived, and nothing accumulated a
        // second suffix (000001-7f3a-b91c.jpg would match no pattern the owner sorts by).
        var loaded = new JsonCatalog().Scan(folder.Path).ToDictionary(r => r.Filename, StringComparer.Ordinal);
        Assert.Equal(6, loaded.Count);
        Assert.All(loaded.Keys, key => Assert.Matches(@"^\d{6}-[0-9a-f]{4}\.[^.]+$", key));
        Assert.Single(loaded.Keys.Select(k => k.Split('-')[1]).Distinct());

        // The schema is frozen (§ 0.2): the desktop app must still open this file.
        Db.RequireV1Schema(folder.Path, "a folder renamed out of Rank Master 2's names is still v1");
        foreach (var (key, row) in Db.Rows(folder.Path))
            Assert.Equal(key, row.Filename);

        // And every rating is still on the picture it was measured against.
        var after = RatingByBytes(folder.Path);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// Every rating in the folder, keyed by the SHA-256 of the picture's bytes. A rename changes every
    /// name, so the bytes are the only handle on "did this rating stay with this picture".
    /// </summary>
    private static Dictionary<string, (double Mu, double Sigma, int Matches, int Impressions, long LastPlayed)>
        RatingByBytes(string folder)
    {
        var result = new Dictionary<string, (double, double, int, int, long)>(StringComparer.Ordinal);
        foreach (var (key, row) in Db.Rows(folder))
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, key));
            result[Convert.ToHexString(SHA256.HashData(bytes))] =
                (row.Mu, row.Sigma, row.Matches, row.Impressions, row.LastPlayed);
        }

        return result;
    }
}
