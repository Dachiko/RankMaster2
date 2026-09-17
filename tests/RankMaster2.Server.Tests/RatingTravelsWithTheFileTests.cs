using System.Text.Json;
using RankMaster2.Catalog;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>
/// A photograph moved to <c>discarded/</c> or <c>special 1/</c> takes its rating with it.
///
/// <para>The owner's reason, 2026-09-17: <i>"We lose nothing if we carry over the rating to the
/// discarded or special folder. I might run the rank master there to see if I discarded something
/// by mistake."</i> Before this, the row was simply deleted on the next save — so a discard folder
/// could not be re-ranked meaningfully, and <c>special 1/</c>, which is where his best pictures go,
/// destroyed exactly the judgements that had cost the most comparisons to earn.</para>
///
/// <para>What is written is an ordinary v1 database in an ordinary folder, so Rank Master 2 opens
/// the subfolder and reads the ratings with no schema change at all.</para>
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public sealed class RatingTravelsWithTheFileTests(Rm2Server server, ITestOutputHelper output) : SessionTestBase(server)
{
    [Theory]
    [InlineData(false, "discarded")]
    [InlineData(true, "special 1")]
    public async Task A_moved_photograph_keeps_its_rating_in_the_subfolder(bool special, string subfolder)
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        // Vote a few times so the file being moved has a rating that is demonstrably not the default.
        var snapshot = opened;
        for (var i = 0; i < 4 && snapshot.IsRanking; i++)
            snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), i % 2 == 0 ? "left" : "right"))
                .ShouldBeSnapshot(200, "vote");

        var movedId = snapshot.Left.Id;

        // § 13.1: a vote is written behind the response, so force the point durable before reading
        // the file. Without this the rating on disk is still the default and the test would be
        // measuring the write-behind rather than the carry.
        await client.SaveAsync();
        var before = new JsonCatalog().Scan(folder.Path).Single(r => r.Filename == movedId);
        var beforeMu = before.Rating.Mu;
        var beforeMatches = before.Matches;
        Assert.True(beforeMatches > 0, "the file must carry real ranking work, or this proves nothing");

        var token = snapshot.RequireToken("ranking");
        var after = (special
                ? await client.SpecialAsync(token, "left", "m1")
                : await client.DiscardAsync(token, "left", "m1"))
            .ShouldBeSnapshot(200, special ? "special" : "discard");
        Assert.DoesNotContain(movedId, after.PairIds);

        // The photograph is in the subfolder ...
        var target = Path.Combine(folder.Path, subfolder);
        Assert.True(File.Exists(Path.Combine(target, movedId)), $"the file should be in {subfolder}/");

        // ... the library's own database no longer lists it (unchanged, specified behaviour) ...
        await client.SaveAsync();
        Assert.DoesNotContain(movedId, new JsonCatalog().Scan(folder.Path).Select(r => r.Filename));

        // ... and the subfolder's own database carries the rating it earned.
        var carriedPath = Path.Combine(target, "rankmaster_db.json");
        Assert.True(File.Exists(carriedPath), $"{subfolder}/ should have its own rankmaster_db.json");

        using var document = JsonDocument.Parse(File.ReadAllText(carriedPath));

        // v1 shape, exactly as the library's own file: version 1, keyed by filename, mu and sigma
        // nested under "rating". This is what lets Rank Master 2 open the subfolder unchanged.
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        var row = document.RootElement.GetProperty("images").GetProperty(movedId);
        Assert.Equal(movedId, row.GetProperty("filename").GetString());
        Assert.Equal(beforeMu, row.GetProperty("rating").GetProperty("mu").GetDouble(), 6);
        Assert.Equal(beforeMatches, row.GetProperty("matches").GetInt32());

        // The desktop app's own read path sees it, which is the point: he can rank the folder there.
        var reopened = new JsonCatalog().Scan(target).Single(r => r.Filename == movedId);
        Assert.Equal(beforeMu, reopened.Rating.Mu, 6);
        Assert.Equal(beforeMatches, reopened.Matches);

        output.WriteLine($"{subfolder}/{movedId}: mu {beforeMu:F3}, {beforeMatches} matches — carried");
    }

    [Fact]
    public async Task Undoing_a_discard_takes_the_row_back_out_of_the_subfolder()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var voted = (await client.VoteAsync(opened.RequireToken("open"), "left"))
            .ShouldBeSnapshot(200, "vote");

        var movedId = voted.Left.Id;
        (await client.DiscardAsync(voted.RequireToken("after vote"), "left", "d1"))
            .ShouldBeSnapshot(200, "discard");

        var target = Path.Combine(folder.Path, "discarded");
        Assert.Contains(movedId, new JsonCatalog().Scan(target).Select(r => r.Filename));

        (await client.UndoAsync("u1")).ShouldBeSnapshot(200, "undo");

        // The file is back in the library, so the subfolder's database must stop claiming it —
        // otherwise discarded/ accumulates rows for photographs that are not there.
        Assert.False(File.Exists(Path.Combine(target, movedId)));
        Assert.DoesNotContain(movedId, new JsonCatalog().Scan(target).Select(r => r.Filename));

        await client.SaveAsync();
        Assert.Contains(movedId, new JsonCatalog().Scan(folder.Path).Select(r => r.Filename));
    }
}
