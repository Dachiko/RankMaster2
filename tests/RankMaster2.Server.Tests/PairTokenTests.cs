using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// The pair token (SERVER_SPEC.md § 8). It is an optimistic-concurrency tag and a duplicate-action
/// guard, and the whole retry story of § 13.3 rests on one property: a token is consumed exactly
/// once, so replaying the *same* token is always safe and replaying after a resync never is.
///
/// § 8.5 is the clause worth the most here — a stale token must cause "no state change of any kind"
/// and must hand back the complete snapshot, because a client that has to poll after a 409 is a
/// client that will double-vote.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class PairTokenTests(Rm2Server server) : SessionTestBase(server)
{
    [Fact]
    public async Task A_stale_token_is_refused_and_changes_nothing_at_all()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var staleToken = opened.RequireToken("open");

        // Consume the token, which makes the one we are holding stale.
        var voted = (await client.VoteAsync(staleToken, "left", "first-vote"))
            .ShouldBeSnapshot(200, "the first vote consumes the token");

        var before = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "state before the stale attempt");

        var response = await client.VoteAsync(staleToken, "right", "replay-with-stale");
        var failure = response.ShouldBeError("stale_pair_token",
            "SERVER_SPEC.md § 8.4: a token that is not the current one is 409 stale_pair_token");

        // § 8.5: details carry both tokens, so a client can see what it was holding and what is live.
        Assert.Equal(staleToken, failure.Detail("suppliedToken", "stale_pair_token details").GetString());
        Assert.Equal(voted.PairToken, failure.Detail("currentToken", "stale_pair_token details").GetString());

        // § 8.5: "the complete SessionSnapshot, identical byte-for-byte to what GET /session would
        // return at that instant". This is what stops the client polling.
        var embedded = failure.RequireSession("a 409 stale_pair_token");
        ContractShape.RequireSnapshot(embedded.Json, "the snapshot embedded in a 409 (SERVER_SPEC.md § 8.5)", response);

        Assert.Equal(before.SessionId, embedded.SessionId);
        Assert.Equal(before.PairSeq, embedded.PairSeq);
        Assert.Equal(before.PairToken, embedded.PairToken);
        Assert.Equal(before.PairIds, embedded.PairIds);
        Assert.Equal(before.SessionVotes, embedded.SessionVotes);
        Assert.Equal(before.Cues, embedded.Cues);

        // § 8.5: "A stale token produces no state change of any kind: no save, no move, no
        // impression, no advance, no pairSeq bump, no lock file touched. It is a pure read."
        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "state after the stale attempt");
        Assert.True(after.PairSeq == before.PairSeq,
            $"SERVER_SPEC.md § 8.5: a stale token must not advance pairSeq. It went from {before.PairSeq} " +
            $"to {after.PairSeq}.");
        Assert.Equal(before.SessionVotes, after.SessionVotes);
        Assert.Equal(before.PairToken, after.PairToken);
        Assert.Equal(before.PairIds, after.PairIds);
        Assert.Equal(before.Cues, after.Cues);
    }

    [Fact]
    public async Task Last_action_tells_a_client_whether_its_lost_request_landed()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var token = opened.RequireToken("open");
        const string requestId = "the-request-that-might-have-been-lost";

        await client.VoteAsync(token, "left", requestId);

        // The client never saw the response. It replays the same token, exactly as § 13.3 tells it
        // to, and the 409 has to be enough to work out what happened.
        var replay = await client.VoteAsync(token, "left", requestId);
        var failure = replay.ShouldBeError("stale_pair_token", "SERVER_SPEC.md § 13.3: replaying the same token");

        var snapshot = failure.RequireSession("the replayed vote");
        var action = snapshot.RequireLastAction("after a vote");

        // § 8.5, first row: clientRequestId matching means that exact request landed once.
        Assert.True(action.ClientRequestId == requestId,
            "SERVER_SPEC.md § 8.5: lastAction.clientRequestId is echoed verbatim, and it is the only thing " +
            $"that lets a client prove its lost request landed. Expected '{requestId}', got " +
            $"'{action.ClientRequestId}'.");
        Assert.Equal(token, action.PairToken);
        Assert.Equal("vote", action.Type);

        // One vote, never two — the point of the whole arrangement.
        Assert.Equal(1, snapshot.SessionVotes);
    }

    [Fact]
    public async Task A_token_from_a_previous_session_is_never_accepted()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var first = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open the first session");
        var oldToken = first.RequireToken("the first session");
        var oldSessionId = first.SessionId;

        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "close");

        var second = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open a second session");

        // § 8.3: pairSeq resets to 0 only when a new session opens — and sessionId changes at the
        // same moment, so old tokens can never collide with new ones.
        Assert.True(second.SessionId != oldSessionId,
            "SERVER_SPEC.md § 9.1: sessionId is new on every POST /session that opens a session.");
        Assert.Equal(0, second.PairSeq);

        var response = await client.VoteAsync(oldToken, "left", "cross-session-replay");
        response.ShouldBeError("stale_pair_token",
            "SERVER_SPEC.md § 8.2: the token is derived from sessionId, so a token from a previous session " +
            "can never be current. § 8.6: the server MUST NOT accept a token from an earlier pairSeq under " +
            "any circumstance.");
    }

    [Theory]
    [InlineData("/session/vote")]
    [InlineData("/session/skip")]
    [InlineData("/session/discard")]
    [InlineData("/session/special")]
    public async Task Every_pair_action_validates_the_token(string path)
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var before = opened.PairSeq;

        object body = path switch
        {
            "/session/vote" => new { pairToken = "definitely-not-the-current-token", winner = "left" },
            "/session/skip" => new { pairToken = "definitely-not-the-current-token" },
            _ => new { pairToken = "definitely-not-the-current-token", side = "left" }
        };

        var response = await client.SendAsync(HttpMethod.Post, path, body);
        var failure = response.ShouldBeError("stale_pair_token",
            $"SERVER_SPEC.md § 8.4: {path} validates the pairToken before touching anything");

        failure.RequireSession($"a 409 on {path}");

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after the bad token");
        Assert.Equal(before, after.PairSeq);
        Assert.Equal(opened.Total, after.Total);
    }

    [Fact]
    public async Task Undo_takes_no_token_and_ignores_one_that_is_sent()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var discarded = (await client.DiscardAsync(opened.RequireToken("open"), "left", "undo-setup"))
            .ShouldBeSnapshot(200, "discard, so there is something to undo");

        // § 10.10: "Sending pairToken in the body is allowed and MUST be ignored." The token below
        // is long stale, which is the whole point — undo reverses a move from a previous pair
        // generation, so requiring a current token would make undo permanently unusable.
        var response = await client.SendAsync(HttpMethod.Post, "/session/undo",
            new { pairToken = "a-stale-token-that-must-be-ignored", clientRequestId = "undo-with-token" });

        var undone = response.ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.10: undo takes no pairToken, and a token sent in the body is ignored — " +
            "not a reason to refuse the request");

        Assert.Equal(discarded.PairSeq + 1, undone.PairSeq);
        Assert.Equal("undo", undone.RequireLastAction("after undo").Type);
    }

    /// <summary>
    /// A move recorded in one folder, then a session opened on another. SERVER_SPEC.md gives two
    /// different answers for this, and they cannot both be right:
    ///
    /// § 10.10 — "The recorded move belongs to another folder → the server clears it and returns
    /// `409 undo_folder_changed`."
    /// § 9.1 — `undoAvailable` is "LastMove is non-null **and** its folder matches the open session.
    /// A `POST /session/undo` while this is `false` returns `409 nothing_to_undo`."
    ///
    /// After a folder change `undoAvailable` is false by § 9.1's own definition, so § 9.1 says
    /// `nothing_to_undo` and § 10.10 says `undo_folder_changed` about the identical state. Either
    /// is defensible; the test accepts both and asserts what actually matters, which is that no file
    /// is moved and the wrong folder's record is not disturbed.
    /// </summary>
    [Fact]
    public async Task Undoing_a_move_that_belongs_to_another_folder_is_refused()
    {
        using var first = LibraryFolder.SixStills();
        using var second = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(first.Path)).ShouldBeSnapshot(201, "open the first folder");
        var discardedId = opened.Left.Id;

        (await client.DiscardAsync(opened.RequireToken("open"), "left", "cross-folder-setup"))
            .ShouldBeSnapshot(200, "discard in the first folder, recording the move");

        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "close the first session");

        var elsewhere = (await client.OpenSessionAsync(second.Path))
            .ShouldBeSnapshot(201, "open a session on a different folder");

        Assert.False(elsewhere.UndoAvailable,
            "SERVER_SPEC.md § 9.1: undoAvailable requires the recorded move's folder to match the open " +
            "session, and it does not.");

        // Both fixture folders use the same filenames, so "did the file appear here" has to be a
        // count rather than a name — the name is in both folders to begin with.
        var secondBefore = Directory.GetFiles(second.Path).Length;

        var response = await client.UndoAsync("cross-folder-undo");
        var failure = response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 10.10 says undo_folder_changed and § 9.1 says nothing_to_undo about this same " +
            "state; either is a legal reading of the contract as written",
            "undo_folder_changed", "nothing_to_undo");

        if (failure.Code == "undo_folder_changed")
            Assert.NotNull(failure.Detail("moveFolder", "undo_folder_changed details").GetString());

        // Whichever code comes back, nothing may have moved.
        Assert.True(File.Exists(Path.Combine(first.Path, "discarded", discardedId)),
            $"SERVER_SPEC.md § 10.10: a refused undo moves nothing, so '{discardedId}' must still be in the " +
            "first folder's discarded/.");

        Assert.True(Directory.GetFiles(second.Path).Length == secondBefore,
            $"SERVER_SPEC.md § 10.10: a refused undo must not move anything into the folder the session is " +
            $"actually open on. It went from {secondBefore} files to {Directory.GetFiles(second.Path).Length}.");

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "the second session after the refusal");
        Assert.Equal(elsewhere.Total, after.Total);
        Assert.Equal(elsewhere.PairSeq, after.PairSeq);
    }

    [Fact]
    public async Task The_validation_order_puts_the_body_check_before_the_token_check()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        await client.OpenSessionAsync(folder.Path);

        // § 8.4 fixes the order: no session → body invalid → exhausted → stale token → apply.
        // So a bad `winner` with a bad token is a 400, not a 409.
        var response = await client.SendAsync(HttpMethod.Post, "/session/vote",
            new { pairToken = "not-the-current-token", winner = "sideways" });

        response.ShouldBeError("invalid_side",
            "SERVER_SPEC.md § 8.4: the body is validated before the token, so a malformed 'winner' is a 400 " +
            "invalid_side even when the token is also wrong");
    }

    [Fact]
    public async Task A_winner_that_is_not_a_side_is_refused()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        foreach (var winner in new[] { "Left", "LEFT", "centre", "0", "" })
        {
            var response = await client.SendAsync(HttpMethod.Post, "/session/vote",
                new { pairToken = opened.RequireToken("open"), winner });

            var failure = response.ShouldBeErrorOneOf(
                $"SERVER_SPEC.md § 5.2: winner must be exactly 'left' or 'right'; '{winner}' is not",
                "invalid_side", "missing_field");

            if (failure.Code == "invalid_side")
                Assert.Equal("winner", failure.Detail("field", "invalid_side details").GetString());
        }

        var unchanged = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after the bad votes");
        Assert.Equal(0, unchanged.PairSeq);
        Assert.Equal(0, unchanged.SessionVotes);
    }

    [Fact]
    public async Task A_client_request_id_over_sixty_four_characters_is_refused()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        var response = await client.VoteAsync(opened.RequireToken("open"), "left", new string('c', 65));
        response.ShouldBeError("invalid_request",
            "SERVER_SPEC.md § 15: clientRequestId is capped at 64 characters, and a breach is 400 invalid_request");
    }

    [Fact]
    public async Task A_vote_with_no_client_request_id_records_a_null_one()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        var voted = (await client.VoteAsync(opened.RequireToken("open"), "right"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.6: clientRequestId is recommended, not required");

        var action = voted.RequireLastAction("after a vote with no client request id");
        Assert.True(action.ClientRequestId is null,
            $"SERVER_SPEC.md § 9.4: clientRequestId is null when the client sent none, got '{action.ClientRequestId}'.");
    }

    [Fact]
    public async Task The_token_is_opaque_and_does_not_repeat_across_a_session()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var seen = new List<string> { snapshot.RequireToken("open") };

        for (var i = 0; i < 5; i++)
        {
            snapshot = (await client.SkipAsync(snapshot.RequireToken($"skip {i}"), $"opaque-{i}"))
                .ShouldBeSnapshot(200, $"skip {i}");
            seen.Add(snapshot.RequireToken($"after skip {i}"));
        }

        Assert.True(seen.Distinct().Count() == seen.Count,
            "SERVER_SPEC.md § 8.2: the token is an HMAC over pairSeq among other things, so it is fresh on " +
            $"every advance. Tokens seen: {string.Join(", ", seen)}");

        // § 8.2: base64url, 22 characters, no padding.
        foreach (var token in seen)
        {
            Assert.True(token.Length == 22,
                $"SERVER_SPEC.md § 8.2: pairToken is base64url truncated to 22 characters (132 bits). " +
                $"'{token}' is {token.Length} characters.");
            Assert.True(token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
                $"SERVER_SPEC.md § 8.2: pairToken is base64url with no padding. '{token}' is not.");
        }
    }
}
