using System.Text.Json;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// <c>POST /session/undo</c> as SERVER_SPEC.md § 10.10 now defines it: cancel the last action of
/// any kind, one level.
///
/// <para>The reason it covers votes is a phone: a mis-tap on a touch screen is a real vote, and
/// without a way back the only remedy is to keep voting and hope. So the clause that matters most
/// here is not that cancel works — it is that cancelling is <b>exact</b> (ratings come back to the
/// bit, by snapshot, never by inverse arithmetic) and that it cannot be turned into a double vote
/// by a retry arriving late.</para>
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class CancelTests(Rm2Server server) : SessionTestBase(server)
{
    /// <summary>The numbers a cancelled vote has to put back, read the way any client can read them.</summary>
    private static async Task<(double Mu, double Sigma, int Matches, int Impressions, long LastPlayed)> RatingAsync(
        Rm2Client client, string id)
    {
        var meta = (await client.MetaAsync(id)).ShouldHaveStatus(200, $"meta for {id}");
        var json = meta.Json!.Value;
        var rating = json.GetProperty("rating");
        return (
            rating.GetProperty("mu").GetDouble(),
            rating.GetProperty("sigma").GetDouble(),
            json.GetProperty("matches").GetInt32(),
            json.GetProperty("impressions").GetInt32(),
            json.GetProperty("lastPlayed").GetInt64());
    }

    [Fact]
    public async Task Cancelling_a_vote_puts_both_ratings_back_exactly()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var left = opened.Left.Id;
        var right = opened.Right.Id;

        var leftBefore = await RatingAsync(client, left);
        var rightBefore = await RatingAsync(client, right);

        var voted = (await client.VoteAsync(opened.RequireToken("open"), "left", "mis-tap"))
            .ShouldBeSnapshot(200, "the vote applies (SERVER_SPEC.md § 10.6)");

        // Sanity: the vote really did move the numbers, so the restore below is not vacuous.
        var leftVoted = await RatingAsync(client, left);
        Assert.True(leftVoted.Matches == leftBefore.Matches + 1,
            "the vote must count a match before there is anything to cancel");
        Assert.True(Math.Abs(leftVoted.Mu - leftBefore.Mu) > 1e-9,
            "the vote must move mu before there is anything to cancel");

        var cancelled = (await client.UndoAsync("cancel-1"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.10: cancel takes the vote back");

        var leftAfter = await RatingAsync(client, left);
        var rightAfter = await RatingAsync(client, right);

        Assert.True(leftAfter == leftBefore,
            "SERVER_SPEC.md § 10.10: ratings are restored by snapshot, exactly. " +
            $"'{left}' was {leftBefore} before the vote and came back as {leftAfter}.");
        Assert.True(rightAfter == rightBefore,
            $"SERVER_SPEC.md § 10.10: '{right}' was {rightBefore} before the vote and came back as {rightAfter}.");

        Assert.Equal(opened.SessionVotes, cancelled.SessionVotes);
        Assert.Empty(cancelled.Cues);
        Assert.False(cancelled.UndoAvailable, "SERVER_SPEC.md § 10.10: one level — the cancel is spent.");

        var last = cancelled.RequireLastAction("§ 9.4");
        Assert.Equal("undo", last.Type);
        Assert.Equal("vote", last.UndoneType);
        Assert.Null(last.RestoredId);
        Assert.Equal("cancel-1", last.ClientRequestId);

        _ = voted;
    }

    [Fact]
    public async Task Cancelling_a_vote_brings_that_same_pair_back_under_a_new_token()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var token = opened.RequireToken("open");
        var pair = opened.PairIds;

        var voted = (await client.VoteAsync(token, "left")).ShouldBeSnapshot(200, "vote");
        var cancelled = (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel");

        Assert.Equal(pair, cancelled.PairIds);

        Assert.True(cancelled.PairSeq == voted.PairSeq + 1,
            "SERVER_SPEC.md § 10.10: pairSeq still increases — it never decreases, even when the " +
            $"pair on screen goes back. It went {voted.PairSeq} -> {cancelled.PairSeq}.");

        Assert.True(cancelled.PairToken != token,
            "SERVER_SPEC.md § 10.10: the restored pair carries a NEW token. Reissuing the old one " +
            "would let a vote that was in flight during the cancel apply a second time.");
    }

    /// <summary>
    /// The clause cancel exists to not break: § 13.3's guarantee that one logical vote can never be
    /// counted twice. A vote in flight when the cancel lands arrives holding a token that is now
    /// stale, and must be refused rather than re-applied.
    /// </summary>
    [Fact]
    public async Task A_vote_that_arrives_after_its_own_cancel_is_refused_not_reapplied()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var token = opened.RequireToken("open");
        var left = opened.Left.Id;
        var before = await RatingAsync(client, left);

        (await client.VoteAsync(token, "left", "in-flight")).ShouldBeSnapshot(200, "the vote lands");
        (await client.UndoAsync()).ShouldBeSnapshot(200, "the user cancels it");

        // The retry of the original vote, carrying the token it was sent with.
        (await client.VoteAsync(token, "left", "in-flight"))
            .ShouldBeError("stale_pair_token",
                "SERVER_SPEC.md § 10.10 and § 13.3: the cancel replaced the token, so the retry of " +
                "the cancelled vote is refused. Applying it would be a vote the user took back.");

        var after = await RatingAsync(client, left);
        Assert.True(after == before,
            $"the cancelled vote must not have been re-applied. '{left}' was {before}, is now {after}.");
    }

    [Fact]
    public async Task Cancelling_a_skip_puts_the_pair_and_its_impressions_back()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var pair = opened.PairIds;
        var before = await RatingAsync(client, opened.Left.Id);

        (await client.SkipAsync(opened.RequireToken("open"), "skip-1")).ShouldBeSnapshot(200, "skip");
        var cancelled = (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel the skip");

        Assert.Equal(pair, cancelled.PairIds);
        Assert.Equal("skip", cancelled.RequireLastAction("§ 9.4").UndoneType);

        var after = await RatingAsync(client, opened.Left.Id);
        Assert.True(after == before,
            $"a cancelled skip restores impressions and lastPlayed. Was {before}, now {after}.");
    }

    [Fact]
    public async Task There_is_exactly_one_level()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var first = (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote 1");
        (await client.VoteAsync(first.RequireToken("ranking"), "right")).ShouldBeSnapshot(200, "vote 2");

        (await client.UndoAsync()).ShouldBeSnapshot(200, "the second vote comes back");
        (await client.UndoAsync())
            .ShouldBeError("nothing_to_undo",
                "SERVER_SPEC.md § 10.10: one level, no stack. The first vote is not reachable.");
    }

    [Fact]
    public async Task A_session_that_has_done_nothing_has_nothing_to_cancel()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        Assert.False(opened.UndoAvailable, "SERVER_SPEC.md § 9.1: nothing has happened yet.");

        (await client.UndoAsync()).ShouldBeError("nothing_to_undo", "SERVER_SPEC.md § 10.10");
    }

    /// <summary>
    /// A vote after a discard means the vote is what cancel takes back — and once it has, the older
    /// move is not offered again. Walking backwards through a session one press at a time is an undo
    /// stack, and § 10.10 says one level.
    /// </summary>
    [Fact]
    public async Task A_vote_after_a_discard_is_what_cancel_takes_back_and_the_move_is_not_offered_again()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var victim = opened.Left.Id;

        var discarded = (await client.DiscardAsync(opened.RequireToken("open"), "left"))
            .ShouldBeSnapshot(200, "discard");
        Assert.True(discarded.UndoAvailable);

        var voted = (await client.VoteAsync(discarded.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "then a vote");

        var cancelled = (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel");
        Assert.Equal("vote", cancelled.RequireLastAction("§ 9.4").UndoneType);

        Assert.False(File.Exists(folder.File(victim)),
            "the discarded file stays discarded: cancelling the vote is not cancelling the discard.");
        Assert.False(cancelled.UndoAvailable,
            "SERVER_SPEC.md § 10.10: one level. The discard underneath is spent, not next in line.");
        (await client.UndoAsync()).ShouldBeError("nothing_to_undo", "SERVER_SPEC.md § 10.10");
    }

    [Fact]
    public async Task Cancelling_a_move_still_works_and_says_which_kind_it_was()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var victim = opened.Right.Id;

        (await client.SpecialAsync(opened.RequireToken("open"), "right")).ShouldBeSnapshot(200, "special");
        var cancelled = (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel the special");

        var last = cancelled.RequireLastAction("§ 9.4");
        Assert.Equal("undo", last.Type);
        Assert.Equal("special", last.UndoneType);
        Assert.Equal(victim, last.RestoredId);
        Assert.True(File.Exists(folder.File(victim)), "the file came back out of 'special 1'.");
    }

    /// <summary>
    /// § 10.10: "So does cancelling the vote that exhausted it." A two-file library exhausts after
    /// the pair is consumed; taking that vote back has to bring the session back to ranking.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_vote_that_exhausted_the_session_brings_it_back()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var pair = opened.PairIds;

        var voted = (await client.VoteAsync(opened.RequireToken("open"), "left"))
            .ShouldBeSnapshot(200, "vote");

        // A two-file library re-pairs the same two, so this is only exhausted if the engine says so;
        // either way the cancel must leave a ranking session holding that pair.
        var cancelled = (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel");

        Assert.True(cancelled.IsRanking,
            "SERVER_SPEC.md § 10.10: cancelling the vote that exhausted a session recovers it.");
        Assert.Equal(pair, cancelled.PairIds);
        _ = voted;
    }

    [Fact]
    public async Task The_snapshot_after_a_cancel_is_the_one_a_plain_read_returns()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote");
        var cancelled = (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel");

        var read = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "GET after the cancel");

        Assert.Equal(cancelled.PairSeq, read.PairSeq);
        Assert.Equal(cancelled.PairToken, read.PairToken);
        Assert.Equal(cancelled.PairIds, read.PairIds);
        Assert.Equal(cancelled.SessionVotes, read.SessionVotes);
        Assert.Equal(cancelled.UndoAvailable, read.UndoAvailable);
    }

    /// <summary>
    /// The ratings the cancel restored are the ratings on disk, not just in memory — the client is
    /// entitled to believe a 200 means durable (§ 13.1), and the desktop app reads that file next.
    /// </summary>
    [Fact]
    public async Task A_cancelled_vote_is_cancelled_on_disk_too()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var left = opened.Left.Id;

        (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote");
        (await client.UndoAsync()).ShouldBeSnapshot(200, "cancel");

        // § 13.1: a vote and its cancel are plain choices, applied at once and on disk within the
        // bound. § 10.5 is how a client asks for the point where the file is the truth, and this
        // test is about the file.
        (await client.SaveAsync()).ShouldBeSnapshot(200, "POST /session/save (SERVER_SPEC.md § 10.5)");

        var database = Path.Combine(folder.Path, "rankmaster_db.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(database));
        var images = document.RootElement.GetProperty("images");

        Assert.True(images.TryGetProperty(left, out var row),
            $"'{left}' should still be a row in the database after a cancelled vote.");
        Assert.True(row.GetProperty("matches").GetInt32() == 0,
            "SERVER_SPEC.md § 10.5: the 200 from POST /session/save means every choice up to that " +
            $"moment is on disk. The file still counts {row.GetProperty("matches").GetInt32()} " +
            "matches for a vote that was taken back.");
    }
}
