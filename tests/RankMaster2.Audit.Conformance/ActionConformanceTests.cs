using System.Text.Json;
using RankMaster2.Audit.Conformance.Audit;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Conformance;

/// <summary>SERVER_SPEC.md § 10.6–10.10 — vote, skip, discard, special, undo.</summary>
public sealed class ActionConformanceTests(Rm2Server server) : AuditTestBase(server)
{
    private static JsonElement Row(string folder, string id)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "rankmaster_db.json")));
        return json.RootElement.GetProperty("images").GetProperty(id).Clone();
    }

    // ---- § 10.6, vote ---------------------------------------------------------------------------

    [Fact]
    public async Task AVoteIsOnDiskBeforeTheResponse()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var (left, right) = opened.RequirePair();

        var after = (await client.VoteAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        Assert.True(File.Exists(Path.Combine(folder.Path, "rankmaster_db.json")),
            "SERVER_SPEC.md § 13.1: \"When a 2xx response leaves the server, every change that request made " +
            "to rankmaster_db.json and to the filesystem is already durably on disk.\"");

        foreach (var id in new[] { left.Id, right.Id })
        {
            var row = Row(folder.Path, id);
            Assert.True(row.GetProperty("matches").GetInt32() == 1,
                $"SERVER_SPEC.md § 10.6: a vote sets \"matches + 1 … on both records\". '{id}' has " +
                $"{row.GetProperty("matches").GetInt32()}.");
            Assert.True(row.GetProperty("impressions").GetInt32() == 1,
                $"SERVER_SPEC.md § 10.6: a vote sets \"impressions + 1 … on both records\". '{id}' has " +
                $"{row.GetProperty("impressions").GetInt32()}.");
            Assert.True(row.GetProperty("lastPlayed").GetInt64() > 0,
                $"SERVER_SPEC.md § 10.6: a vote sets \"lastPlayed = now\" on both records. '{id}' has " +
                $"{row.GetProperty("lastPlayed").GetInt64()}.");
        }

        Assert.Equal(1, after.SessionVotes);
        Assert.True(after.Cues.Length == 1,
            "SERVER_SPEC.md § 10.6: a vote appends exactly one cue. Got " +
            $"[{string.Join(",", after.Cues)}].");
    }

    [Fact]
    public async Task AVoteMovesTheWinnersRatingUpAndTheLosersDown()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var (left, right) = opened.RequirePair();

        await client.VoteAsync(opened.RequireToken("§ 8"), "left");

        var winner = Row(folder.Path, left.Id).GetProperty("rating");
        var loser = Row(folder.Path, right.Id).GetProperty("rating");

        Assert.True(winner.GetProperty("mu").GetDouble() > 25.0,
            "SPEC.md § Ranking: the TrueSkill update raises the winner's μ from the 25.0 default. " +
            $"Got {winner.GetProperty("mu").GetDouble()}.");
        Assert.True(loser.GetProperty("mu").GetDouble() < 25.0,
            "SPEC.md § Ranking: the loser's μ falls. Got " + loser.GetProperty("mu").GetDouble() + ".");
    }

    [Fact]
    public async Task TheFirstVoteOfASessionIsAConfirmation()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var after = (await client.VoteAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        Assert.True(after.Cues.SequenceEqual(new[] { "confirmation" }),
            "SPEC.md § Match strip and SERVER_SPEC.md § 10.6: the cue is Confirmation \"if the winner's μ ≥ " +
            "the loser's μ *before* the update\". Both start at μ = 25, so the first vote of a fresh " +
            $"library is always a confirmation. Got [{string.Join(",", after.Cues)}].");
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public async Task VoteRecordsTheWinnerInLastAction(string winner)
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var after = (await client.VoteAsync(opened.RequireToken("§ 8"), winner, "req-1"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        var last = after.RequireLastAction("SERVER_SPEC.md § 9.4");
        Assert.Equal("vote", last.Type);
        Assert.Equal(winner, last.Winner);
        Assert.Equal("req-1", last.ClientRequestId);
        Assert.True(last.SideValue is null,
            "SERVER_SPEC.md § 9.4: `side` is \"discard and special only\" and must be null for a vote.");
        Assert.True(last.Id is null,
            "SERVER_SPEC.md § 9.4: `id` is for discard, special and drop_missing; a vote leaves it null.");
        Assert.True(last.RestoredId is null,
            "SERVER_SPEC.md § 9.4: `restoredId` is \"undo only\".");
        Assert.Equal(after.PairSeq, last.Seq);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("LEFT")]
    [InlineData("")]
    [InlineData("l")]
    public async Task AnUnrecognisedWinnerIsInvalidSide(string winner)
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var response = await client.VoteAsync(opened.RequireToken("§ 8"), winner);
        var error = response.Error("invalid_side",
            "SERVER_SPEC.md § 5.2: \"`side`/`winner` is not `left` or `right`\" → 400 invalid_side. " +
            "openapi.yaml's Side enum is [left, right] exactly, so case variants are not accepted");

        var details = response.Details(error, "SERVER_SPEC.md § 5.2: invalid_side carries {field, value}");
        Assert.Equal("winner", details.GetProperty("field").GetString());
        Assert.Equal(winner, details.GetProperty("value").GetString());
    }

    [Fact]
    public async Task AVoteWithNoWinnerIsMissingField()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var response = await client.SendAsync(HttpMethod.Post, "/session/vote",
            $"{{\"pairToken\":{JsonSerializer.Serialize(opened.RequireToken("§ 8"))}}}");

        var error = response.Error("missing_field",
            "openapi.yaml makes `winner` required on VoteRequest; SERVER_SPEC.md § 5.2 answers 400 missing_field");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2");
        Assert.Equal("winner", details.GetProperty("field").GetString());
    }

    [Fact]
    public async Task AVoteWithNoPairTokenIsMissingField()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var response = await client.SendAsync(HttpMethod.Post, "/session/vote", "{\"winner\":\"left\"}");
        var error = response.Error("missing_field",
            "openapi.yaml makes `pairToken` required on VoteRequest; SERVER_SPEC.md § 5.2 answers 400 missing_field");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2");
        Assert.Equal("pairToken", details.GetProperty("field").GetString());
    }

    [Fact]
    public async Task AWinnerOfTheWrongJsonTypeIsInvalidRequest()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var response = await client.SendAsync(HttpMethod.Post, "/session/vote",
            $"{{\"pairToken\":{JsonSerializer.Serialize(opened.RequireToken("§ 8"))},\"winner\":7}}");

        response.Error("invalid_request",
            "SERVER_SPEC.md § 5.2: \"a field has the wrong JSON type\" → 400 invalid_request. A number " +
            "where the Side enum belongs is a type error, not an unrecognised side value");
    }

    [Fact]
    public async Task AClientRequestIdOverSixtyFourCharactersIsRejected()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var response = await client.VoteAsync(opened.RequireToken("§ 8"), "left", new string('a', 65));
        var error = response.Error("invalid_request",
            "SERVER_SPEC.md § 15: \"clientRequestId | 64 characters | 400 invalid_request\"");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2: invalid_request carries details.field");
        Assert.Equal("clientRequestId", details.GetProperty("field").GetString());
    }

    [Fact]
    public async Task AClientRequestIdOfExactlySixtyFourCharactersIsAccepted()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var id = new string('a', 64);
        var after = (await client.VoteAsync(opened.RequireToken("§ 8"), "left", id))
            .RequireSnapshot(200,
                "SERVER_SPEC.md § 15 caps clientRequestId at 64 characters, so exactly 64 is inside the limit");

        Assert.Equal(id, after.RequireLastAction("§ 9.4").ClientRequestId);
    }

    // ---- § 10.7, skip ---------------------------------------------------------------------------

    [Fact]
    public async Task ASkipCountsAnImpressionAndNothingElse()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var (left, right) = opened.RequirePair();

        var after = (await client.SkipAsync(opened.RequireToken("§ 8"), "skip-1"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.7");

        foreach (var id in new[] { left.Id, right.Id })
        {
            var row = Row(folder.Path, id);
            Assert.True(row.GetProperty("impressions").GetInt32() == 1,
                $"SERVER_SPEC.md § 10.7: skip is \"impressions + 1 on both\". '{id}' has " +
                $"{row.GetProperty("impressions").GetInt32()}.");
            Assert.True(row.GetProperty("matches").GetInt32() == 0,
                $"SERVER_SPEC.md § 10.7: skip makes \"no matches change\". '{id}' has " +
                $"{row.GetProperty("matches").GetInt32()}.");
            Assert.True(row.GetProperty("lastPlayed").GetInt64() == 0,
                $"SERVER_SPEC.md § 10.7: skip makes \"no lastPlayed change\". '{id}' has " +
                $"{row.GetProperty("lastPlayed").GetInt64()}.");
            Assert.True(Math.Abs(row.GetProperty("rating").GetProperty("mu").GetDouble() - 25.0) < 1e-9,
                "SERVER_SPEC.md § 10.7: skip makes \"no rating change\". μ moved to " +
                $"{row.GetProperty("rating").GetProperty("mu").GetDouble()}.");
        }

        Assert.True(after.SessionVotes == 0,
            $"SERVER_SPEC.md § 10.7: skip makes \"no SessionVotes change\". Got {after.SessionVotes}.");
        Assert.True(after.Cues.Length == 0,
            $"SERVER_SPEC.md § 10.7: skip appends \"no cue\". Got [{string.Join(",", after.Cues)}].");

        var last = after.RequireLastAction("§ 9.4");
        Assert.Equal("skip", last.Type);
        Assert.True(last.Winner is null, "SERVER_SPEC.md § 9.4: `winner` is `vote` only.");
        Assert.Equal("skip-1", last.ClientRequestId);
    }

    [Fact]
    public async Task SkipRejectsAWinnerFieldItDoesNotHave()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var response = await client.SendAsync(HttpMethod.Post, "/session/skip", "{}");
        var error = response.Error("missing_field",
            "openapi.yaml makes `pairToken` required on SkipRequest");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2");
        Assert.Equal("pairToken", details.GetProperty("field").GetString());
    }

    // ---- § 10.8, discard ------------------------------------------------------------------------

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public async Task DiscardMovesTheNamedSideAndDropsTheRecord(string side)
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var target = opened.Side(side).Id;
        var after = (await client.DiscardAsync(opened.RequireToken("§ 8"), side, "d-1"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");

        Assert.False(File.Exists(Path.Combine(folder.Path, target)),
            $"SERVER_SPEC.md § 10.8: discard moves '{target}' out of the folder.");
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", target)),
            $"SERVER_SPEC.md § 10.8: the destination is <folder>/discarded/ (created on demand).");

        Assert.True(after.Total == opened.Total - 1,
            $"SERVER_SPEC.md § 10.8: the record is gone. counts.total went {opened.Total} → {after.Total}.");

        var last = after.RequireLastAction("§ 9.4");
        Assert.Equal("discard", last.Type);
        Assert.Equal(side, last.SideValue);
        Assert.Equal(target, last.Id);
        Assert.True(last.Winner is null, "SERVER_SPEC.md § 9.4: `winner` is vote only.");
        Assert.True(last.RestoredId is null, "SERVER_SPEC.md § 9.4: `restoredId` is undo only.");
        Assert.True(after.UndoAvailable,
            "SERVER_SPEC.md § 9.1: undoAvailable is true when \"LibraryActions.LastMove is non-null and its " +
            "folder matches the open session\", which a discard has just made so.");
    }

    [Fact]
    public async Task SpecialMovesToSpecialOneAndNeverCreatesSpecialTwo()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var target = opened.Left.Id;
        var after = (await client.SpecialAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.9");

        Assert.True(File.Exists(Path.Combine(folder.Path, "special 1", target)),
            "SERVER_SPEC.md § 10.9: the destination is `<folder>/special 1/`.");
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "special 2")),
            "SERVER_SPEC.md § 10.9 and SPEC.md § File actions: \"special 2 MUST NOT be created\".");

        Assert.Equal("special", after.RequireLastAction("§ 9.4").Type);
    }

    [Fact]
    public async Task DiscardDoesNotCountAnImpressionForEitherItem()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var survivor = opened.Right.Id;

        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");

        var row = Row(folder.Path, survivor);
        Assert.True(row.GetProperty("impressions").GetInt32() == 0,
            "SERVER_SPEC.md § 7.4.9: \"A discard does not mark the survivor as recently shown and does not " +
            $"count an impression for either item.\" '{survivor}' has {row.GetProperty("impressions").GetInt32()}.");
        Assert.True(row.GetProperty("matches").GetInt32() == 0,
            "SERVER_SPEC.md § 7.4.9: a discard is not a match.");
    }

    [Fact]
    public async Task DiscardUniquifiesACollisionInTheDestination()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var target = opened.Left.Id;

        // Something is already sitting in discarded/ under that exact name.
        Directory.CreateDirectory(Path.Combine(folder.Path, "discarded"));
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "discarded", target), "an older file");

        (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");

        var expected = Path.Combine(folder.Path, "discarded",
            $"{Path.GetFileNameWithoutExtension(target)} (2){Path.GetExtension(target)}");
        Assert.True(File.Exists(expected),
            "SERVER_SPEC.md § 10.8: FileOps.MoveToSubfolder \"uniquifies on collision as `name (2).ext`\". " +
            $"Expected '{expected}'; discarded/ holds " +
            $"[{string.Join(", ", Directory.GetFiles(Path.Combine(folder.Path, "discarded")).Select(Path.GetFileName))}].");
        Assert.Equal("an older file",
            await File.ReadAllTextAsync(Path.Combine(folder.Path, "discarded", target)));
    }

    [Theory]
    [InlineData("up")]
    [InlineData("Left")]
    public async Task AnUnrecognisedDiscardSideIsInvalidSide(string side)
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var response = await client.DiscardAsync(opened.RequireToken("§ 8"), side);
        var error = response.Error("invalid_side", "SERVER_SPEC.md § 5.2");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2: invalid_side carries {field, value}");
        Assert.True(details.GetProperty("field").GetString() == "side",
            "SERVER_SPEC.md § 5.2: `details.field` names the field that was wrong — `side` on discard, " +
            $"`winner` on vote. Got '{details.GetProperty("field").GetString()}'.");
        Assert.Equal(side, details.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DiscardWithNoSideIsMissingField()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var response = await client.SendAsync(HttpMethod.Post, "/session/discard",
            $"{{\"pairToken\":{JsonSerializer.Serialize(opened.RequireToken("§ 8"))}}}");

        var error = response.Error("missing_field", "openapi.yaml makes `side` required on SideRequest");
        Assert.Equal("side", response.Details(error, "SERVER_SPEC.md § 5.2").GetProperty("field").GetString());
    }

    [Fact]
    public async Task DiscardIgnoresAnIdInTheBody()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var offScreen = MediaFixtures.PlainPng;
        var expected = opened.Left.Id;

        var response = await client.SendAsync(HttpMethod.Post, "/session/discard",
            new { pairToken = opened.RequireToken("§ 8"), side = "left", id = offScreen });

        var after = response.RequireSnapshot(200,
            "SERVER_SPEC.md § 2: unknown JSON fields are ignored, so an `id` in the body is not an error");

        Assert.Equal(expected, after.RequireLastAction("§ 9.4").Id);
        Assert.True(!File.Exists(Path.Combine(folder.Path, "discarded", offScreen)),
            "SERVER_SPEC.md § 10.8: \"The id is taken from the pair the token names — the client never " +
            "sends an id, so it cannot act on an item that is not on screen.\"");
    }

    // ---- § 10.8, the missing-file special case ----------------------------------------------------

    [Fact]
    public async Task DiscardingAVanishedFileDropsTheRecordWithoutAMove()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var target = opened.Left.Id;
        File.Delete(Path.Combine(folder.Path, target));

        var after = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200,
                "SERVER_SPEC.md § 10.8: \"if the source file does not exist when the move is attempted, the " +
                "server MUST NOT call FileOps.MoveToSubfolder (which would throw FileNotFoundException) and " +
                "MUST instead call RankingSession.Drop(id) and Save() directly, returning 200\"");

        var last = after.RequireLastAction("§ 9.4");
        Assert.True(last.Type == "drop_missing",
            "SERVER_SPEC.md § 10.8: the missing-file discard reports \"lastAction.type = drop_missing\". " +
            $"Got '{last.Type}'.");
        Assert.Equal(target, last.Id);
        Assert.True(last.PairToken is null,
            "SERVER_SPEC.md § 9.4: lastAction.pairToken is \"Null for undo and drop_missing, which do not " +
            $"take one\". Got '{last.PairToken}'.");
        Assert.False(after.UndoAvailable,
            "SERVER_SPEC.md § 10.8: \"No undo entry is recorded — there is no file to put back — so " +
            "undoAvailable is left as it was\", and nothing was undoable before this call.");
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "discarded")),
            "SERVER_SPEC.md § 10.8: no move is attempted, so `discarded/` is never created.");

        Assert.True(after.Total == opened.Total - 1,
            $"SERVER_SPEC.md § 10.8: the record is dropped. counts.total {opened.Total} → {after.Total}.");
        Assert.DoesNotContain(target, after.PairIds);
    }

    [Fact]
    public async Task DropMissingLeavesAnEarlierUndoEntryIntact()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var discarded = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");
        Assert.True(discarded.UndoAvailable);

        var target = discarded.Left.Id;
        File.Delete(Path.Combine(folder.Path, target));

        var after = (await client.DiscardAsync(discarded.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8, missing-file case");

        Assert.Equal("drop_missing", after.RequireLastAction("§ 9.4").Type);
        Assert.True(after.UndoAvailable,
            "SERVER_SPEC.md § 10.8: on the missing-file path \"undoAvailable is left as it was\" — the " +
            "earlier discard is still the recorded move and is still undoable.");
    }

    [Fact]
    public async Task ACorruptButPresentFileIsAnOrdinaryDiscard()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var target = opened.Left.Id;
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, target), MediaFixtures.CorruptJpeg());

        var after = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");

        Assert.True(after.RequireLastAction("§ 9.4").Type == "discard",
            "SERVER_SPEC.md § 10.8: \"A file that is present but *corrupt* is not this case: the move " +
            $"succeeds and the discard is ordinary.\" Got '{after.RequireLastAction("§ 9.4").Type}'.");
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", target)),
            "SERVER_SPEC.md § 10.8: a corrupt file still moves.");
    }

    // ---- § 7.1 / § 7.2, exhaustion -----------------------------------------------------------------

    [Fact]
    public async Task ExhaustedRefusesEveryPairAction()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var exhausted = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");
        Assert.True(exhausted.IsExhausted, $"Expected `exhausted`; got '{exhausted.State}'.");

        var seq = exhausted.PairSeq;

        foreach (var response in new[]
                 {
                     await client.VoteAsync("anything", "left"),
                     await client.SkipAsync("anything"),
                     await client.DiscardAsync("anything", "left"),
                     await client.SpecialAsync("anything", "right")
                 })
        {
            var error = response.Error("no_current_pair",
                "SERVER_SPEC.md § 7.2: \"exhausted | vote / skip / discard / special | exhausted | " +
                "unchanged | 409 no_current_pair\"");
            Assert.True(error.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object,
                "SERVER_SPEC.md § 4: error.session \"MUST be present for every 409 on a /session/* endpoint\".\n" +
                response.Describe());
        }

        var after = (await client.GetSessionAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.2");
        Assert.True(after.PairSeq == seq,
            $"SERVER_SPEC.md § 7.2: a refused action leaves pairSeq unchanged. {seq} → {after.PairSeq}.");
    }

    [Fact]
    public async Task SaveIsStillAllowedWhileExhausted()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");

        (await client.SaveAsync())
            .RequireSnapshot(200,
                "SERVER_SPEC.md § 10.5: save is \"Always allowed while a session is open, including in " +
                "`exhausted`\"");
    }

    // ---- § 10.10, undo --------------------------------------------------------------------------

    [Fact]
    public async Task UndoWithNothingRecordedIsNothingToUndo()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        Assert.False(opened.UndoAvailable);

        var response = await client.UndoAsync();
        var error = response.Error("nothing_to_undo",
            "SERVER_SPEC.md § 10.10: \"No recorded move → 409 nothing_to_undo (with error.session)\"");

        Assert.True(error.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object,
            "SERVER_SPEC.md § 10.10 says nothing_to_undo comes \"with error.session\", and § 4 requires the " +
            "snapshot on every 409 on a /session/* endpoint.\n" + response.Describe());
    }

    [Fact]
    public async Task UndoRestoresTheFileAndTheRecord()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var target = opened.Left.Id;
        var discarded = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");

        var undone = (await client.UndoAsync("undo-1"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.10");

        Assert.True(File.Exists(Path.Combine(folder.Path, target)),
            $"SERVER_SPEC.md § 10.10: \"the file is moved back from discarded/\". '{target}' is not there.");
        Assert.False(File.Exists(Path.Combine(folder.Path, "discarded", target)));
        Assert.True(undone.Total == opened.Total,
            $"SERVER_SPEC.md § 10.10: \"RankingSession.Restore puts the record back\". counts.total is " +
            $"{undone.Total}, was {opened.Total} before the discard.");

        var last = undone.RequireLastAction("§ 9.4");
        Assert.Equal("undo", last.Type);
        Assert.Equal(target, last.Id);
        Assert.Equal(target, last.RestoredId);
        Assert.True(last.PairToken is null,
            "SERVER_SPEC.md § 9.4: lastAction.pairToken is \"Null for undo and drop_missing, which do not " +
            $"take one\". Got '{last.PairToken}'.");
        Assert.Equal("undo-1", last.ClientRequestId);
        Assert.False(undone.UndoAvailable,
            "SERVER_SPEC.md § 10.10: \"LastMove is cleared → 200, pairSeq +1, undoAvailable now false\".");
        Assert.Equal(discarded.PairSeq + 1, undone.PairSeq);
    }

    [Fact]
    public async Task UndoReplacesTheCurrentPair()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var discarded = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");
        var tokenBefore = discarded.RequireToken("§ 8");

        var undone = (await client.UndoAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.10");

        Assert.True(undone.PairToken != tokenBefore,
            "SERVER_SPEC.md § 10.10.1 and § 7.4.4: \"The current pair is replaced, even if it was " +
            "perfectly valid. pairToken changes.\"");
    }

    [Fact]
    public async Task ASecondUndoIsNothingToUndo()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");
        (await client.UndoAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.10");

        (await client.UndoAsync())
            .Error("nothing_to_undo",
                "SERVER_SPEC.md § 13.3: \"a second undo is 409 nothing_to_undo; it cannot undo two moves, " +
                "because there is only ever one recorded move\"");
    }

    [Fact]
    public async Task UndoTakesNoPairTokenAndIgnoresOne()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");

        // A token from two generations ago: § 10.10 requires it to be ignored, not validated.
        var response = await client.SendAsync(HttpMethod.Post, "/session/undo",
            new { pairToken = opened.RequireToken("§ 8"), clientRequestId = "u-1" });

        response.RequireSnapshot(200,
            "SERVER_SPEC.md § 10.10: \"Sending pairToken in the body is allowed and MUST be ignored\" — " +
            "the move being reversed belongs to an earlier pair generation, so any token the client holds " +
            "is stale by construction and requiring one would make undo permanently unusable");
    }

    [Fact]
    public async Task UndoWorksWithNoBodyAtAll()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");

        var response = await client.SendAsync(HttpMethod.Post, "/session/undo");
        response.RequireSnapshot(200,
            "openapi.yaml marks the undo request body `required: false`, so a POST with no body at all " +
            "must work");
    }

    [Fact]
    public async Task UndoEscapesExhaustion()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var exhausted = (await client.DiscardAsync(opened.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.8");
        Assert.True(exhausted.IsExhausted);

        var undone = (await client.UndoAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.10");
        Assert.True(undone.IsRanking,
            "SERVER_SPEC.md § 10.10: \"Restore recovers the session from exhausted back to ranking when it " +
            $"brings the eligible count back to two.\" State is '{undone.State}'.");
        Assert.True(undone.PairToken is not null,
            "SERVER_SPEC.md § 9.1: pairToken is non-null once the session is ranking again.");
    }

    [Fact]
    public async Task UndoRestoringUnderANewNameReportsBothIds()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var target = opened.Left.Id;
        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");

        // Someone puts a different file back under the discarded name before the undo lands.
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, target), MediaFixtures.Jpeg(64, 64));

        var undone = (await client.UndoAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.10");
        var last = undone.RequireLastAction("§ 9.4");

        var expected = $"{Path.GetFileNameWithoutExtension(target)} (2){Path.GetExtension(target)}";
        Assert.True(last.RestoredId == expected,
            "SERVER_SPEC.md § 10.10.2: \"If the original name has since been taken, FileOps.UniqueFileName " +
            "gives the file back as `name (2).ext` and the record's id changes with it. lastAction.id holds " +
            $"the old id, lastAction.restoredId the new one.\" id='{last.Id}', restoredId='{last.RestoredId}', " +
            $"expected restoredId '{expected}'.");
        Assert.True(last.Id == target,
            $"SERVER_SPEC.md § 10.10.2: lastAction.id keeps the old id. Got '{last.Id}'.");
        Assert.True(File.Exists(Path.Combine(folder.Path, expected)),
            $"The restored file must actually be on disk as '{expected}'.");
    }

    [Fact]
    public async Task UndoAfterTheFolderChangedIsRefusedAndClearsTheMove()
    {
        using var first = LibraryFolder.SixStills();
        using var second = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(first.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        await client.DiscardAsync(opened.RequireToken("§ 8"), "left");
        await client.CloseSessionAsync();

        var other = (await client.OpenSessionAsync(second.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        Assert.False(other.UndoAvailable,
            "SERVER_SPEC.md § 9.1: undoAvailable is \"LibraryActions.LastMove is non-null **and** its folder " +
            "matches the open session\". The recorded move belongs to a folder that is no longer open.");

        var response = await client.UndoAsync();
        var code = response.ErrorCodeOrThrow("SERVER_SPEC.md § 4");
        Assert.True(code is "undo_folder_changed" or "nothing_to_undo",
            "SERVER_SPEC.md § 10.10: \"The recorded move belongs to another folder → the server clears it " +
            $"and returns 409 undo_folder_changed\" (or nothing_to_undo if the move was already cleared). " +
            $"Got '{code}'.\n{response.Describe()}");

        if (code == "undo_folder_changed")
        {
            var error = response.Error("undo_folder_changed", "SERVER_SPEC.md § 10.10");
            var details = response.Details(error,
                "SERVER_SPEC.md § 5.4: undo_folder_changed carries details.moveFolder");
            Assert.Equal(Path.GetFullPath(first.Path), details.GetProperty("moveFolder").GetString());

            (await client.UndoAsync())
                .Error("nothing_to_undo",
                    "SERVER_SPEC.md § 10.10: undo_folder_changed \"clears it\", so the next undo has nothing " +
                    "left to reverse");
        }
    }

    // ---- § 13.3, replay safety ----------------------------------------------------------------------

    [Fact]
    public async Task ReplayingTheSameTokenNeverDoubleVotes()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");
        var (left, right) = opened.RequirePair();
        var token = opened.RequireToken("§ 8");

        await client.VoteAsync(token, "left", "same-logical-action");
        (await client.VoteAsync(token, "left", "same-logical-action"))
            .Error("stale_pair_token", "SERVER_SPEC.md § 13.3");
        (await client.VoteAsync(token, "left", "same-logical-action"))
            .Error("stale_pair_token", "SERVER_SPEC.md § 13.3");

        foreach (var id in new[] { left.Id, right.Id })
        {
            var matches = Row(folder.Path, id).GetProperty("matches").GetInt32();
            Assert.True(matches == 1,
                "SERVER_SPEC.md § 13.3: \"replaying the same pairToken is always safe, because a token is " +
                $"consumed exactly once … Either way one vote, never two.\" '{id}' has matches = {matches}.");
        }
    }
}
