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
}
