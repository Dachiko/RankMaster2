using System.Text.Json;
using RankMaster2.Audit.Conformance.Audit;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Conformance;

/// <summary>SERVER_SPEC.md § 8 — the pair token: shape, lifetime, and what a stale one costs.</summary>
public sealed class PairTokenConformanceTests(Rm2Server server) : AuditTestBase(server)
{
    [Fact]
    public async Task TheTokenIsTwentyTwoBase64UrlCharacters()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var snapshot = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var token = snapshot.RequireToken("SERVER_SPEC.md § 8");
        Assert.True(token.Length == 22,
            "SERVER_SPEC.md § 8.2: \"pairToken = base64url(mac) truncated to the first 22 characters — " +
            $"132 bits, no padding\". Got {token.Length} characters ('{token}').");
        Assert.True(Check.IsBase64Url(token),
            $"SERVER_SPEC.md § 8.2: the token is base64url with no padding. Got '{token}'.");
    }

    [Fact]
    public async Task TheSessionIdIsTwentyTwoBase64UrlCharacters()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var snapshot = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        Assert.True(snapshot.SessionId.Length == 22,
            "SERVER_SPEC.md § 9.1: sessionId is \"Opaque, 22 chars, base64url\". Got " +
            $"{snapshot.SessionId.Length} ('{snapshot.SessionId}').");
        Assert.True(Check.IsBase64Url(snapshot.SessionId),
            $"SERVER_SPEC.md § 9.1: sessionId is base64url. Got '{snapshot.SessionId}'.");
    }

    [Fact]
    public async Task PairSeqStartsAtZeroAndRisesByExactlyOnePerAction()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var snapshot = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        Assert.True(snapshot.PairSeq == 0,
            "SERVER_SPEC.md § 8.2: \"pairSeq is a 64-bit unsigned integer … starting at 0 when the session " +
            $"opens\". Got {snapshot.PairSeq}.");

        var steps = new List<(string What, long Seq)> { ("open", snapshot.PairSeq) };

        snapshot = (await client.VoteAsync(snapshot.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");
        steps.Add(("vote", snapshot.PairSeq));

        snapshot = (await client.SkipAsync(snapshot.RequireToken("§ 8")))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.7");
        steps.Add(("skip", snapshot.PairSeq));

        snapshot = (await client.DiscardAsync(snapshot.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");
        steps.Add(("discard", snapshot.PairSeq));

        snapshot = (await client.UndoAsync())
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.10");
        steps.Add(("undo", snapshot.PairSeq));

        snapshot = (await client.SpecialAsync(snapshot.RequireToken("§ 8"), "right"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.9");
        steps.Add(("special", snapshot.PairSeq));

        for (var i = 1; i < steps.Count; i++)
            Assert.True(steps[i].Seq == steps[i - 1].Seq + 1,
                "SERVER_SPEC.md § 8.3: \"pairSeq MUST be incremented by exactly 1, and never by anything " +
                $"else\" on vote, skip, discard, special and undo. Sequence: " +
                string.Join(" → ", steps.Select(s => $"{s.What}={s.Seq}")));
    }

    [Fact]
    public async Task ReadsAndSaveLeavePairSeqAlone()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        await client.GetSessionAsync();
        await client.GetPairAsync();
        await client.SaveAsync();
        await client.OpenSessionAsync(folder.Path);
        await client.MetaAsync(opened.PairIds[0]);
        await client.StillAsync(opened.PairIds[0]);

        var after = (await client.GetSessionAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.2");
        Assert.True(after.PairSeq == opened.PairSeq,
            "SERVER_SPEC.md § 8.3: \"POST /session/save, any GET | — | unchanged | still valid\". " +
            $"pairSeq moved {opened.PairSeq} → {after.PairSeq}.");
    }

    [Fact]
    public async Task TheTokenChangesEvenWhenATwoFileLibraryRepeatsThePair()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var first = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var second = (await client.VoteAsync(first.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        Assert.True(second.PairIds.OrderBy(x => x).SequenceEqual(first.PairIds.OrderBy(x => x)),
            "SERVER_SPEC.md § 7.4.1: \"With exactly two eligible files the next pair is the same two ids, " +
            "possibly swapped, forever.\" This fixture is the two-file case and the ids changed, so the " +
            "premise of the next assertion does not hold.");

        Assert.True(second.PairToken != first.PairToken,
            "SERVER_SPEC.md § 7.4.1: \"pairToken changes anyway — this is why the token MUST include " +
            "pairSeq and MUST NOT be a hash of the ids alone.\" Both snapshots carry " +
            $"'{first.PairToken}'.");
    }

    [Fact]
    public async Task WarmPairsAreEmptyOnATwoFileLibrary()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();
        var snapshot = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        Assert.True(snapshot.WarmPairCount == 0,
            "SERVER_SPEC.md § 9.5: \"With exactly two eligible files it is always empty: every id is " +
            $"reserved by Current, so Pick() has nothing left to return.\" Got {snapshot.WarmPairCount}.");
    }

    // ---- § 8.4 / § 8.5, a stale token ------------------------------------------------------------

    [Fact]
    public async Task AStaleTokenIs409WithBothTokensAndTheWholeSnapshot()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var stale = opened.RequireToken("§ 8");

        var current = (await client.VoteAsync(stale, "left", "first-vote"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        var replay = await client.VoteAsync(stale, "left", "first-vote");
        var error = replay.Error("stale_pair_token",
            "SERVER_SPEC.md § 8.6: \"The server MUST NOT accept a token from an earlier pairSeq under any " +
            "circumstance, including an obvious retry\"");

        var details = replay.Details(error,
            "SERVER_SPEC.md § 5.4: stale_pair_token carries {suppliedToken, currentToken}");
        Assert.True(details.GetProperty("suppliedToken").GetString() == stale,
            "SERVER_SPEC.md § 8.5: \"error.details.suppliedToken — echoed verbatim\".");
        Assert.True(details.GetProperty("currentToken").GetString() == current.PairToken,
            "SERVER_SPEC.md § 8.5: \"error.details.currentToken — the live token, or null when exhausted\".");

        Assert.True(error.TryGetProperty("session", out var embedded) && embedded.ValueKind == JsonValueKind.Object,
            "SERVER_SPEC.md § 8.5: \"error.session — the complete SessionSnapshot (§ 9), identical " +
            "byte-for-byte to what GET /session would return at that instant\".\n" + replay.Describe());

        // The embedded snapshot must be the whole thing, not a summary.
        replay.RequireKeys(embedded, Contract.SnapshotKeys, "error.session",
            "SERVER_SPEC.md § 8.5: the embedded snapshot is the complete § 9.1 object, because it is what " +
            "resynchronises the client in one round trip — \"A client MUST NOT poll GET /session after a 409\"");

        var live = await client.GetSessionAsync();
        Assert.True(JsonSerializer.Serialize(embedded) == JsonSerializer.Serialize(live.JsonBody),
            "SERVER_SPEC.md § 8.5: error.session is \"identical byte-for-byte to what GET /session would " +
            $"return at that instant\".\n  error.session : {JsonSerializer.Serialize(embedded)}\n" +
            $"  GET /session  : {JsonSerializer.Serialize(live.JsonBody)}");
    }

    [Fact]
    public async Task AStaleTokenChangesNothingAtAll()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var stale = opened.RequireToken("§ 8");

        var afterVote = (await client.VoteAsync(stale, "left", "one"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        var before = (await client.GetSessionAsync()).Text;
        var databaseBefore = await File.ReadAllBytesAsync(Path.Combine(folder.Path, "rankmaster_db.json"));

        foreach (var attempt in new[]
                 {
                     await client.VoteAsync(stale, "right", "two"),
                     await client.SkipAsync(stale, "three"),
                     await client.DiscardAsync(stale, "left", "four"),
                     await client.SpecialAsync(stale, "right", "five")
                 })
        {
            attempt.Error("stale_pair_token", "SERVER_SPEC.md § 8.4.4");
        }

        var after = (await client.GetSessionAsync()).Text;
        Assert.True(before == after,
            "SERVER_SPEC.md § 8.5: \"A stale token produces no state change of any kind: no save, no move, " +
            "no impression, no advance, no pairSeq bump, no lock file touched. It is a pure read.\"\n" +
            $"  before : {before}\n  after  : {after}");

        Assert.True(databaseBefore.SequenceEqual(
                await File.ReadAllBytesAsync(Path.Combine(folder.Path, "rankmaster_db.json"))),
            "SERVER_SPEC.md § 8.5: a stale token performs no save.");

        Assert.False(Directory.Exists(Path.Combine(folder.Path, "discarded")),
            "SERVER_SPEC.md § 8.5: a stale discard moves nothing, so `discarded/` must not even be created.");
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "special 1")),
            "SERVER_SPEC.md § 8.5: a stale special moves nothing.");

        Assert.Equal(afterVote.PairSeq, new Snapshot(JsonDocument.Parse(after).RootElement,
            await client.GetSessionAsync()).PairSeq);
    }

    [Fact]
    public async Task LastActionIdentifiesTheRequestThatConsumedTheToken()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var token = opened.RequireToken("§ 8");

        await client.VoteAsync(token, "left", "request-alpha");

        var replay = await client.VoteAsync(token, "left", "request-alpha");
        var error = replay.Error("stale_pair_token", "SERVER_SPEC.md § 8.5");
        var session = error.GetProperty("session");
        var last = session.GetProperty("lastAction");

        Assert.True(last.GetProperty("clientRequestId").GetString() == "request-alpha",
            "SERVER_SPEC.md § 8.5: \"lastAction.clientRequestId equals the id the client just sent → that " +
            "exact request landed once\". This is the only thing that lets a client prove its lost request " +
            $"landed. Got '{last.GetProperty("clientRequestId")}'.");
        Assert.True(last.GetProperty("pairToken").GetString() == token,
            "SERVER_SPEC.md § 9.4: lastAction.pairToken is \"The token this action consumed\". Got " +
            $"'{last.GetProperty("pairToken")}'.");
        Assert.Equal("vote", last.GetProperty("type").GetString());
        Assert.Equal("left", last.GetProperty("winner").GetString());
    }

    [Fact]
    public async Task AnActionFromAPreviousSessionIsNeverAccepted()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var first = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var oldToken = first.RequireToken("§ 8");
        var oldSessionId = first.SessionId;

        await client.CloseSessionAsync();
        var second = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        Assert.True(second.SessionId != oldSessionId,
            "SERVER_SPEC.md § 9.1: sessionId is \"New on every POST /session that opens a session\". " +
            $"Both sessions report '{oldSessionId}'.");
        Assert.True(second.PairSeq == 0,
            "SERVER_SPEC.md § 8.3: \"It resets to 0 only when a new session opens\". Got " +
            $"{second.PairSeq}.");

        var replay = await client.VoteAsync(oldToken, "left");
        Assert.True(replay.StatusCode == 409,
            "SERVER_SPEC.md § 8.3: \"sessionId changes at the same moment, so old tokens from a previous " +
            $"session can never collide with new ones\". Replaying '{oldToken}' answered " +
            $"{replay.StatusCode}.\n{replay.Describe()}");
        replay.Error("stale_pair_token", "SERVER_SPEC.md § 8.4.4");
    }

    // ---- § 8.4, the validation order ---------------------------------------------------------------

    [Fact]
    public async Task AMalformedBodyIsRejectedBeforeTheToken()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        // A bad body *and* a bad token: § 8.4 orders body validation (step 2) before the token
        // check (step 4), so this must be a 400, not a 409.
        var response = await client.SendAsync(HttpMethod.Post, "/session/vote",
            new { pairToken = "definitely-not-current", winner = "sideways" });

        var error = response.Error("invalid_side",
            "SERVER_SPEC.md § 8.4 fixes the order: \"2. Body invalid → 400 per § 5.2\" comes before " +
            "\"4. pairToken != current → 409 stale_pair_token\"");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2: invalid_side carries {field, value}");
        Assert.Equal("winner", details.GetProperty("field").GetString());
        Assert.Equal("sideways", details.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("/session/vote", "{ not json at all")]
    [InlineData("/session/skip", "{ not json at all")]
    [InlineData("/session/discard", "{ not json at all")]
    [InlineData("/session/special", "{ not json at all")]
    public async Task NoSessionIsRejectedBeforeAMalformedBody(string path, string body)
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, path, body);

        response.Error("no_session",
            "SERVER_SPEC.md § 8.4 fixes the validation order for every mutating pair action, \"in this " +
            "exact order, inside the session lock, before touching anything: 1. No session → " +
            "404 no_session. 2. Body invalid → 400 per § 5.2.\" With no session open the caller must be " +
            "told to reopen one, not that its body is bad — a client that branches on the code (§ 4: " +
            "\"Clients MUST branch on this\") will retry the body instead of reopening the session");
    }

    [Theory]
    [InlineData("/session/vote", "{}")]
    [InlineData("/session/vote", "{\"pairToken\":\"x\",\"winner\":\"sideways\"}")]
    [InlineData("/session/discard", "{\"pairToken\":\"x\",\"side\":\"up\"}")]
    public async Task NoSessionIsRejectedBeforeAWellFormedButInvalidBody(string path, string body)
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, path, body);

        response.Error("no_session",
            "SERVER_SPEC.md § 8.4: step 1 is \"No session → 404 no_session\"; every § 5.2 body failure is " +
            "step 2 and must not pre-empt it");
    }

    [Fact]
    public async Task ExhaustionIsReportedBeforeAStaleToken()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var staleToken = opened.RequireToken("§ 8");

        var exhausted = (await client.DiscardAsync(staleToken, "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");
        Assert.True(exhausted.IsExhausted,
            "Discarding one of two files leaves one eligible record, which § 7.1 calls `exhausted`. " +
            $"State is '{exhausted.State}'.");

        var response = await client.VoteAsync(staleToken, "left");
        response.Error("no_current_pair",
            "SERVER_SPEC.md § 8.4 fixes the order: \"3. state == exhausted → 409 no_current_pair\" comes " +
            "before \"4. pairToken != current → 409 stale_pair_token\". The token supplied here is stale " +
            "as well, so a server that checked the token first would answer stale_pair_token");
    }

    [Fact]
    public async Task WhenExhaustedTheCurrentTokenInAStaleErrorIsNull()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var exhausted = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");

        Assert.True(exhausted.PairToken is null,
            "SERVER_SPEC.md § 8.2: \"When state is exhausted, pairToken is null. There is nothing to sign.\"");
        Assert.True(exhausted.PairJson is null,
            "SERVER_SPEC.md § 9.1: pair is null when state is exhausted.");
    }
}
