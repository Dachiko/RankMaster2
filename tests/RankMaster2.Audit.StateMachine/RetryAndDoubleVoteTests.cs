using System.Text.Json;
using RankMaster2.Audit.StateMachine.Harness;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.StateMachine;

/// <summary>
/// SERVER_SPEC.md § 8.4, § 8.5, § 8.6 and § 13.3.
///
/// The rule the whole retry story rests on: "replaying the *same* pairToken is always safe, because
/// a token is consumed exactly once". This suite spends a token twice, spends it after the pair has
/// moved for some other reason, spends one from a previous session, and checks the record's own
/// counters afterwards — because "the vote was not counted twice" is a fact about
/// <c>matches</c>/<c>impressions</c> on disk, not about the status code.
/// </summary>
public sealed class RetryAndDoubleVoteTests(Rm2Server server) : AuditTestBase(server)
{
    private async Task<(int Matches, int Impressions)> CountersAsync(Rm2Client client, string id)
    {
        var meta = (await client.MetaAsync(id))
            .ShouldHaveStatus(200, $"GET /media/{id}/meta reports the record's counters (§ 12.1)");
        var json = meta.JsonBody;
        return (json.GetProperty("matches").GetInt32(), json.GetProperty("impressions").GetInt32());
    }

    [Fact]
    public async Task The_same_token_cannot_vote_twice_and_the_second_call_resynchronises_the_client()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");
        var (left, right) = (opened.Left.Id, opened.Right.Id);

        var first = (await client.VoteAsync(token, "left", "req-A"))
            .ShouldBeSnapshot(200, "the first vote lands");
        Assert.Equal(1, first.SessionVotes);

        var replay = await client.VoteAsync(token, "left", "req-A");
        var error = replay.ShouldBeError(
            "stale_pair_token",
            "SERVER_SPEC.md § 8.6: a token is valid only until the next pairSeq increment, " +
            "including for an obvious retry");

        // § 8.5: the details name both tokens and the envelope carries the whole snapshot.
        Assert.Equal(token, error.Detail("suppliedToken", "§ 8.5").GetString());
        var resync = error.RequireSession("§ 8.5: this single response fully resynchronises the client");
        var currentToken = error.Detail("currentToken", "§ 8.5").GetString();
        Assert.Equal(resync.PairToken, currentToken);
        Assert.Equal(first.PairSeq, resync.PairSeq);
        Assert.Equal(1, resync.SessionVotes);

        // § 8.5 row 1: clientRequestId matches, so the client knows its request landed exactly once.
        var last = resync.RequireLastAction("§ 8.5 needs lastAction to disambiguate a lost response");
        Assert.Equal("req-A", last.ClientRequestId);
        Assert.Equal(token, last.PairToken);
        Assert.Equal("vote", last.Type);
        Assert.Equal("left", last.Winner);

        // The fact behind the status code.
        var (leftMatches, leftImpressions) = await CountersAsync(client, left);
        var (rightMatches, rightImpressions) = await CountersAsync(client, right);
        Assert.Equal(1, leftMatches);
        Assert.Equal(1, rightMatches);
        Assert.Equal(1, leftImpressions);
        Assert.Equal(1, rightImpressions);
    }

    [Fact]
    public async Task A_token_replayed_after_the_pair_moved_for_another_reason_is_refused_without_a_state_change()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var stale = opened.RequireToken("ranking");

        // The pair moves on for a reason that is not this client's vote.
        var afterSkip = (await client.SkipAsync(stale, "req-skip"))
            .ShouldBeSnapshot(200, "a skip advances the pair");
        var afterVote = (await client.VoteAsync(afterSkip.RequireToken("ranking"), "right", "req-vote"))
            .ShouldBeSnapshot(200, "and so does a vote");

        var before = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "§ 10.2");

        var error = (await client.VoteAsync(stale, "left", "req-old"))
            .ShouldBeError("stale_pair_token", "§ 8.6: the server never accepts a token from an earlier pairSeq");

        // § 8.5: "A stale token produces no state change of any kind ... It is a pure read."
        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "§ 10.2");
        Assert.Equal(before.PairSeq, after.PairSeq);
        Assert.Equal(before.SessionVotes, after.SessionVotes);
        Assert.Equal(before.PairToken, after.PairToken);
        Assert.Equal(before.Cues, after.Cues);
        Assert.Equal(before.LastSavedAt, after.LastSavedAt);
        Assert.Equal(
            before.RequireLastAction("§ 9.4").ClientRequestId,
            after.RequireLastAction("§ 9.4").ClientRequestId);

        // § 8.5 row 3: neither the id nor the token matches, so the client knows it never landed.
        var session = error.RequireSession("§ 8.5");
        var last = session.RequireLastAction("§ 9.4");
        Assert.NotEqual("req-old", last.ClientRequestId);
        Assert.NotEqual(stale, last.PairToken);
        Assert.Equal(afterVote.PairSeq, session.PairSeq);
    }

    [Fact]
    public async Task A_token_from_a_previous_session_on_the_same_folder_is_refused()
    {
        using var folder = AuditFolder.SixStills();
        var first = await OpenAsync(folder);
        var client = await ClientAsync();
        var oldToken = first.RequireToken("ranking");
        Assert.Equal(0, first.PairSeq);

        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "§ 10.4");
        var second = await OpenAsync(folder);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(0, second.PairSeq);

        // pairSeq is back at 0 and the pair may well be the same two ids. Only the per-session
        // secret keeps the old token from being accepted (§ 8.3, last paragraph).
        Assert.NotEqual(oldToken, second.PairToken);
        (await client.VoteAsync(oldToken, "left", "req-old-session"))
            .ShouldBeError("stale_pair_token", "§ 8.3: a token from a previous session must never be honoured");
        (await client.GetSessionAsync()).ShouldBeSnapshot(200, "§ 10.2")
            .Let(s => Assert.Equal(0, s.SessionVotes));
    }

    /// <summary>
    /// § 8.4 fixes the order: exhausted is answered before the token is even looked at. A client
    /// holding a token for a pair that no longer exists must be told <c>no_current_pair</c>, because
    /// that is the state it has to recover from, not <c>stale_pair_token</c>.
    /// </summary>
    [Fact]
    public async Task An_exhausted_session_answers_no_current_pair_before_it_looks_at_the_token()
    {
        using var folder = AuditFolder.TwoStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var exhausted = (await client.DiscardAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "discarding down to one file exhausts the session (§ 7.1)");
        Assert.True(exhausted.IsExhausted);
        Assert.Null(exhausted.PairToken);

        foreach (var (name, call) in new (string, Func<Task<Rm2Response>>)[]
                 {
                     ("vote", () => client.VoteAsync("obviously-not-a-token", "left")),
                     ("skip", () => client.SkipAsync("obviously-not-a-token")),
                     ("discard", () => client.DiscardAsync("obviously-not-a-token", "left")),
                     ("special", () => client.SpecialAsync("obviously-not-a-token", "right")),
                 })
        {
            (await call()).ShouldBeError(
                "no_current_pair",
                $"SERVER_SPEC.md § 8.4 step 3 runs before step 4, so /{name} on an exhausted session " +
                "is no_current_pair whatever the token says");
        }
    }

    /// <summary>
    /// § 8.4 also puts body validation ahead of the token check, and § 8.5 makes the stale-token
    /// answer a pure read. Neither may cost the client its place.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_is_refused_before_the_token_is_consumed()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("ranking");

        (await client.SendAsync(HttpMethod.Post, "/session/vote", new { pairToken = token, winner = "middle" }))
            .ShouldBeError("invalid_side", "§ 5.2: 'winner' must be left or right");
        (await client.SendAsync(HttpMethod.Post, "/session/vote", new { winner = "left" }))
            .ShouldBeError("missing_field", "§ 5.2: pairToken is required");

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "§ 10.2");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(token, after.PairToken);

        (await client.VoteAsync(token, "left"))
            .ShouldBeSnapshot(200, "§ 8.4: a rejected body leaves the token unspent");
    }

    /// <summary>
    /// § 13.3: a discard is retried with the *same* token, and either the retry moves the file or it
    /// is told the first one did. Never both, and never a second file gone.
    /// </summary>
    [Fact]
    public async Task A_discard_replayed_with_the_same_token_cannot_move_a_second_file()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("ranking");
        var victim = opened.Left.Id;

        var landed = (await client.DiscardAsync(token, "left", "req-discard"))
            .ShouldBeSnapshot(200, "the discard lands");
        Assert.Equal(5, landed.Total);

        var error = (await client.DiscardAsync(token, "left", "req-discard"))
            .ShouldBeError("stale_pair_token", "§ 13.3: the replay is refused, it does not move another file");
        var resync = error.RequireSession("§ 8.5");
        Assert.Equal(5, resync.Total);
        Assert.Equal(landed.PairSeq, resync.PairSeq);
        Assert.Equal("req-discard", resync.RequireLastAction("§ 9.4").ClientRequestId);

        var discarded = Directory.GetFiles(Path.Combine(folder.Path, "discarded"));
        Assert.Single(discarded);
        Assert.Equal(victim, Path.GetFileName(discarded[0]));
    }

    /// <summary>
    /// § 9.4: <c>clientRequestId</c> is "echoed verbatim from the request body, or null if the client
    /// sent none". A client that sends none is the one § 8.5 leaves worst off, so the null has to be
    /// a real null rather than an empty string a comparison would match.
    /// </summary>
    [Fact]
    public async Task A_client_request_id_is_echoed_verbatim_and_absent_means_null()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var withId = (await client.VoteAsync(opened.RequireToken("ranking"), "left", "Ünïcode-Ид-42"))
            .ShouldBeSnapshot(200, "a vote with a clientRequestId");
        Assert.Equal("Ünïcode-Ид-42", withId.RequireLastAction("§ 9.4").ClientRequestId);

        var withoutId = (await client.SendAsync(
                HttpMethod.Post, "/session/vote",
                new { pairToken = withId.RequireToken("ranking"), winner = "right" }))
            .ShouldBeSnapshot(200, "a vote with no clientRequestId");
        Assert.Null(withoutId.RequireLastAction("§ 9.4").ClientRequestId);

        var empty = (await client.VoteAsync(withoutId.RequireToken("ranking"), "left", ""))
            .ShouldBeSnapshot(200, "a vote with an empty clientRequestId");
        Assert.Null(empty.RequireLastAction("§ 9.4").ClientRequestId);
    }

    /// <summary>
    /// § 10.5 and the § 7.2 transition table: save and the two reads never move the pair, so a token
    /// held across them stays spendable. This is what lets a client save before backgrounding.
    /// </summary>
    [Fact]
    public async Task Reads_and_saves_never_spend_the_token()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("ranking");

        for (var i = 0; i < 3; i++)
        {
            (await client.GetSessionAsync()).ShouldBeSnapshot(200, "§ 10.2");
            (await client.GetPairAsync()).ShouldBeSnapshot(200, "§ 10.3");
            (await client.SaveAsync()).ShouldBeSnapshot(200, "§ 10.5");
            (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(200, "§ 10.1 step 2: the same folder is a pure read");
        }

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "§ 10.2");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(token, after.PairToken);
        Assert.Equal(opened.PairIds, after.PairIds);
        (await client.VoteAsync(token, "left")).ShouldBeSnapshot(200, "the token is still the current one");
    }
}

internal static class SnapshotExtensions
{
    public static void Let(this Snapshot snapshot, Action<Snapshot> check) => check(snapshot);
}
