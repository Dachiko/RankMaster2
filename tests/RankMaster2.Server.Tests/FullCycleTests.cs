using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>
/// The whole cycle over the real HTTP surface: pair, open, read the pair, fetch the stills, vote,
/// skip, discard, special, undo, save, close.
///
/// Each step checks the thing the contract promises about *that* step — that a vote counts an
/// impression on both records and a skip does too but without a match, that a discard moves a file
/// and an undo brings it back, that `pairSeq` moves exactly once per successful mutation. Run in one
/// test, in order, because the interesting properties are the ones that only hold across steps.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class FullCycleTests(Rm2Server server, ITestOutputHelper output) : SessionTestBase(server)
{
    [Fact]
    public async Task The_whole_ranking_cycle()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        // ---- pair -------------------------------------------------------------------------
        // Already done once for the whole suite; a code is single-use (§ 10.11), so this asserts
        // the credential works rather than re-pairing.
        var credentials = await Server.AuthenticateAsync();
        output.WriteLine($"paired: {credentials.Mode} — {credentials.Diagnostic.Split('\n')[0]}");

        // ---- open -------------------------------------------------------------------------
        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201,
            "SERVER_SPEC.md § 10.1: opening a rankable folder");
        output.WriteLine($"opened {opened.FolderName}: {opened.Total} files, policy {opened.Policy}, " +
                         $"pair {string.Join(" vs ", opened.PairIds)}");

        Assert.Equal(0, opened.PairSeq);
        Assert.Equal(6, opened.Unranked);

        // ---- read the pair ----------------------------------------------------------------
        var read = (await client.GetPairAsync()).ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.3: GET /session/pair");
        Assert.Equal(opened.PairToken, read.PairToken);
        Assert.Equal(0, read.PairSeq);

        // § 7.4.10: reading a pair is not an impression.
        var leftBefore = await ReadMetaAsync(client, read.Left.Id);
        Assert.Equal(0, leftBefore.Impressions);
        Assert.Equal(0, leftBefore.Matches);

        // ---- fetch the stills, through the links the server built -------------------------
        foreach (var reference in new[] { read.Left, read.Right })
        {
            var still = await client.FollowAsync(reference.StillLink!);
            still.ShouldHaveStatus(200,
                $"SERVER_SPEC.md § 9.3: the links in a snapshot are usable verbatim ({reference.Id})");
            Assert.True(still.Body.Length > 0, $"The still for {reference.Id} came back empty.");

            var thumb = await client.FollowAsync(reference.ThumbLink!);
            thumb.ShouldHaveStatus(200, $"SERVER_SPEC.md § 12.1: the thumbnail for {reference.Id}");

            output.WriteLine($"fetched {reference.Id}: still {still.Body.Length} B " +
                             $"({still.ContentType}), thumb {thumb.Body.Length} B");
        }

        // Fetching bytes is not an impression either (§ 7.4.10).
        Assert.Equal(0, (await ReadMetaAsync(client, read.Left.Id)).Impressions);

        // ---- vote -------------------------------------------------------------------------
        var votedFor = read.Left.Id;
        var votedAgainst = read.Right.Id;

        var voted = (await client.VoteAsync(read.RequireToken("before vote"), "left", "cycle-vote-1"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.6: a vote returns the new snapshot");
        output.WriteLine($"voted left ({votedFor}) → pairSeq {voted.PairSeq}, cues [{string.Join(",", voted.Cues)}]");

        Assert.Equal(opened.PairSeq + 1, voted.PairSeq);
        Assert.Equal(1, voted.SessionVotes);
        Assert.Single(voted.Cues);
        Assert.Contains(voted.Cues[0], new[] { "confirmation", "upset" });
        Assert.NotEqual(read.PairToken, voted.PairToken);

        var action = voted.RequireLastAction("after a vote");
        Assert.Equal("vote", action.Type);
        Assert.Equal("left", action.Winner);
        Assert.Equal("cycle-vote-1", action.ClientRequestId);
        Assert.Equal(read.PairToken, action.PairToken);
        Assert.Equal(voted.PairSeq, action.Seq);
        Assert.Null(action.SideValue);

        // § 10.6: both records get matches + 1, impressions + 1 and lastPlayed = now.
        foreach (var id in new[] { votedFor, votedAgainst })
        {
            var meta = await ReadMetaAsync(client, id);
            Assert.True(meta.Matches == 1,
                $"SERVER_SPEC.md § 10.6: a vote sets matches + 1 on both records. {id} has matches {meta.Matches}.");
            Assert.True(meta.Impressions == 1,
                $"SERVER_SPEC.md § 10.6: a vote sets impressions + 1 on both records. {id} has {meta.Impressions}.");
            Assert.True(meta.LastPlayed > 0,
                $"SERVER_SPEC.md § 10.6: a vote sets lastPlayed on both records. {id} has {meta.LastPlayed}.");
        }

        // § 13.1: a 200 is proof of durability — the JSON is already on disk.
        Assert.True(File.Exists(folder.File("rankmaster_db.json")),
            "SERVER_SPEC.md § 13.1: when a 2xx leaves the server, the change is already durably on disk. " +
            "The vote returned 200 and rankmaster_db.json does not exist.");
        Assert.True(voted.LastSavedAt is not null,
            "SERVER_SPEC.md § 9.1: lastSavedAt records this session's last successful save.");

        // ---- skip -------------------------------------------------------------------------
        var beforeSkip = (await client.GetPairAsync()).ShouldBeSnapshot(200, "the pair before a skip");
        var skippedIds = beforeSkip.PairIds;
        var matchesBefore = await Task.WhenAll(skippedIds.Select(id => ReadMetaAsync(client, id)));

        var skipped = (await client.SkipAsync(beforeSkip.RequireToken("before skip"), "cycle-skip-1"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.7: a skip returns the new snapshot");
        output.WriteLine($"skipped {string.Join(" vs ", skippedIds)} → pairSeq {skipped.PairSeq}");

        Assert.Equal(beforeSkip.PairSeq + 1, skipped.PairSeq);
        Assert.Equal("skip", skipped.RequireLastAction("after a skip").Type);

        // § 10.7: impressions + 1 on both, no rating change, no matches change, no cue, no vote count.
        Assert.True(skipped.SessionVotes == voted.SessionVotes,
            $"SERVER_SPEC.md § 9.1: sessionVotes counts votes only. It moved from {voted.SessionVotes} " +
            $"to {skipped.SessionVotes} across a skip.");
        Assert.True(skipped.Cues.Length == voted.Cues.Length,
            "SERVER_SPEC.md § 9.1: skip appends no cue.");

        for (var i = 0; i < skippedIds.Length; i++)
        {
            var after = await ReadMetaAsync(client, skippedIds[i]);
            Assert.True(after.Impressions == matchesBefore[i].Impressions + 1,
                $"SERVER_SPEC.md § 10.7: a skip is impressions + 1 on both. {skippedIds[i]} went from " +
                $"{matchesBefore[i].Impressions} to {after.Impressions}.");
            Assert.True(after.Matches == matchesBefore[i].Matches,
                $"SERVER_SPEC.md § 10.7: a skip makes no matches change. {skippedIds[i]} went from " +
                $"{matchesBefore[i].Matches} to {after.Matches}.");
            Assert.True(Math.Abs(after.Mu - matchesBefore[i].Mu) < 1e-9,
                $"SERVER_SPEC.md § 10.7: a skip makes no rating change. {skippedIds[i]} μ moved from " +
                $"{matchesBefore[i].Mu} to {after.Mu}.");
        }

        // ---- discard ----------------------------------------------------------------------
        var beforeDiscard = (await client.GetPairAsync()).ShouldBeSnapshot(200, "the pair before a discard");
        var discardedId = beforeDiscard.Left.Id;
        var survivorId = beforeDiscard.Right.Id;

        var discarded = (await client.DiscardAsync(beforeDiscard.RequireToken("before discard"), "left", "cycle-discard-1"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.8: a discard returns the new snapshot");
        output.WriteLine($"discarded {discardedId} → pairSeq {discarded.PairSeq}, {discarded.Total} files left");

        Assert.Equal(beforeDiscard.PairSeq + 1, discarded.PairSeq);
        Assert.Equal(beforeDiscard.Total - 1, discarded.Total);

        var discardAction = discarded.RequireLastAction("after a discard");
        Assert.Equal("discard", discardAction.Type);
        Assert.Equal("left", discardAction.SideValue);
        Assert.Equal(discardedId, discardAction.Id);
        Assert.Null(discardAction.Winner);

        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", discardedId)),
            $"SERVER_SPEC.md § 10.8: discard moves the file to <folder>/discarded/. '{discardedId}' is not there.");
        Assert.False(folder.Has(discardedId),
            $"SERVER_SPEC.md § 10.8: the file is moved, not copied. '{discardedId}' is still in the folder.");

        Assert.True(discarded.UndoAvailable,
            "SERVER_SPEC.md § 9.1: a recorded move in this folder makes undoAvailable true.");

        // § 11.2 step 4: a discarded id is no longer a record.
        var gone = await client.MetaAsync(discardedId);
        gone.ShouldBeError("unknown_media_id",
            "SERVER_SPEC.md § 11.3: a file discarded through the API is removed from records, so its id is a 404");

        // § 7.4.9: the survivor is not marked as recently shown and may appear again immediately.
        var survivorMeta = await ReadMetaAsync(client, survivorId);
        output.WriteLine($"survivor {survivorId}: matches {survivorMeta.Matches}, impressions {survivorMeta.Impressions}");

        // ---- special ----------------------------------------------------------------------
        var beforeSpecial = (await client.GetPairAsync()).ShouldBeSnapshot(200, "the pair before a special");
        var specialId = beforeSpecial.Right.Id;

        var special = (await client.SpecialAsync(beforeSpecial.RequireToken("before special"), "right", "cycle-special-1"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.9: special is discard with a different destination");
        output.WriteLine($"special {specialId} → pairSeq {special.PairSeq}, {special.Total} files left");

        Assert.Equal(beforeSpecial.PairSeq + 1, special.PairSeq);
        Assert.Equal("special", special.RequireLastAction("after a special").Type);
        Assert.Equal(specialId, special.RequireLastAction("after a special").Id);

        Assert.True(File.Exists(Path.Combine(folder.Path, "special 1", specialId)),
            $"SERVER_SPEC.md § 10.9: special moves the file to '<folder>/special 1/'. '{specialId}' is not there.");
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "special 2")),
            "SERVER_SPEC.md § 10.9 and SPEC.md § File actions: 'special 2' MUST NOT be created.");

        // ---- undo -------------------------------------------------------------------------
        var beforeUndo = (await client.GetPairAsync()).ShouldBeSnapshot(200, "the pair before an undo");
        Assert.True(beforeUndo.UndoAvailable, "The special just moved a file, so undo is available.");

        var undone = (await client.UndoAsync("cycle-undo-1")).ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.10: undo reverses the last successful move");
        output.WriteLine($"undid the special → pairSeq {undone.PairSeq}, {undone.Total} files");

        Assert.Equal(beforeUndo.PairSeq + 1, undone.PairSeq);

        var undoAction = undone.RequireLastAction("after an undo");
        Assert.Equal("undo", undoAction.Type);
        Assert.Equal(specialId, undoAction.Id);
        Assert.True(undoAction.RestoredId is not null,
            "SERVER_SPEC.md § 9.4: undo reports restoredId — the name the file actually came back as.");
        Assert.True(undoAction.PairToken is null,
            "SERVER_SPEC.md § 9.4: lastAction.pairToken is null for undo, which consumes no token.");

        Assert.False(undone.UndoAvailable,
            "SERVER_SPEC.md § 10.10: one level, no stack — after an undo there is nothing left to undo.");
        Assert.True(folder.Has(undoAction.RestoredId!),
            $"SERVER_SPEC.md § 10.10: the file is moved back into the folder as '{undoAction.RestoredId}'.");
        Assert.Equal(beforeUndo.Total + 1, undone.Total);

        // § 7.4.4 and § 10.10: the current pair is replaced even though it was valid.
        Assert.NotEqual(beforeUndo.PairToken, undone.PairToken);

        var secondUndo = await client.UndoAsync("cycle-undo-2");
        secondUndo.ShouldBeError("nothing_to_undo",
            "SERVER_SPEC.md § 10.10 and § 13.3: a second undo is 409 nothing_to_undo — it cannot undo two moves");

        // ---- save -------------------------------------------------------------------------
        var beforeSave = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "before an explicit save");

        var saved = (await client.SaveAsync()).ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.5: POST /session/save is the Ctrl+S equivalent");
        output.WriteLine($"saved at {saved.LastSavedAt}");

        // § 10.5: never changes the pair, the token or pairSeq.
        Assert.Equal(beforeSave.PairSeq, saved.PairSeq);
        Assert.Equal(beforeSave.PairToken, saved.PairToken);
        Assert.Equal(beforeSave.PairIds, saved.PairIds);
        Assert.NotNull(saved.LastSavedAt);

        var savedAgain = (await client.SaveAsync()).ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.5: save is idempotent");
        Assert.Equal(saved.PairSeq, savedAgain.PairSeq);

        // ---- close ------------------------------------------------------------------------
        var close = await client.CloseSessionAsync();
        close.ShouldHaveStatus(204, "SERVER_SPEC.md § 10.4: DELETE /session");
        output.WriteLine("closed");

        Assert.False(File.Exists(folder.File(".rankmaster.lock")),
            "SERVER_SPEC.md § 10.4: closing removes the lock file.");

        (await client.GetSessionAsync()).ShouldBeError("no_session",
            "SERVER_SPEC.md § 7.1: after closing, every /session* call is 404 no_session");
    }

    [Fact]
    public async Task Voting_a_two_file_library_keeps_re_pairing_the_same_two_files()
    {
        // § 7.4.1: Advance() sets Current = null before Pick() precisely so a two-file library can
        // re-pair. The ids repeat forever; the token changes anyway, which is why the token must
        // include pairSeq rather than being a hash of the ids.
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open a two-file folder");
        var seenTokens = new HashSet<string> { snapshot.RequireToken("open") };
        var ids = snapshot.PairIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        for (var round = 0; round < 4; round++)
        {
            var token = snapshot.RequireToken($"round {round}");
            snapshot = (await client.VoteAsync(token, round % 2 == 0 ? "left" : "right", $"two-file-{round}"))
                .ShouldBeSnapshot(200, $"vote {round} on a two-file library");

            Assert.Equal("ranking", snapshot.State);
            Assert.Equal(ids, snapshot.PairIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());

            var newToken = snapshot.RequireToken($"after round {round}");
            Assert.True(seenTokens.Add(newToken),
                $"SERVER_SPEC.md § 7.4.1 and § 8.2: pairToken must change on every advance even when the " +
                $"pair's ids repeat. Round {round} returned a token that has already been seen.");

            Assert.Equal(round + 1, (int)snapshot.PairSeq);
            Assert.Equal(0, snapshot.WarmPairCount);
        }

        Assert.Equal(4, snapshot.SessionVotes);
    }

    [Fact]
    public async Task Discarding_down_to_one_file_exhausts_the_session_and_undo_recovers_it()
    {
        using var folder = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open a two-file folder");
        var discardedId = snapshot.Left.Id;

        var exhausted = (await client.DiscardAsync(snapshot.RequireToken("open"), "left", "exhaust-1"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.8: discarding one of two files");

        // § 7.1: exhausted is reached when discards leave fewer than two eligible records.
        Assert.Equal("exhausted", exhausted.State);
        Assert.Null(exhausted.PairToken);
        Assert.Null(exhausted.PairJson);
        Assert.Equal(0, exhausted.WarmPairCount);

        // § 7.2: in exhausted, every pair action is 409 no_current_pair.
        foreach (var (path, body) in new (string, object)[]
                 {
                     ("/session/vote", new { pairToken = "anything", winner = "left" }),
                     ("/session/skip", new { pairToken = "anything" }),
                     ("/session/discard", new { pairToken = "anything", side = "left" }),
                     ("/session/special", new { pairToken = "anything", side = "right" }),
                 })
        {
            var response = await client.SendAsync(HttpMethod.Post, path, body);
            var failure = response.ShouldBeError("no_current_pair",
                $"SERVER_SPEC.md § 8.4 step 3: in 'exhausted', {path} is 409 no_current_pair — and that check " +
                "comes before the token check, so the token is never even looked at");

            failure.RequireSession($"a 409 on {path}");
        }

        // § 10.5: save is still allowed while exhausted.
        (await client.SaveAsync()).ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.5: save is allowed while exhausted");

        // § 10.10: Restore recovers the session from exhausted back to ranking.
        var recovered = (await client.UndoAsync("exhaust-undo"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.10: undo restores the file");

        Assert.Equal("ranking", recovered.State);
        Assert.NotNull(recovered.PairToken);
        Assert.Equal(2, recovered.Rankable);
        Assert.Equal(exhausted.PairSeq + 1, recovered.PairSeq);

        var restoredId = recovered.RequireLastAction("after the recovering undo").RestoredId;
        Assert.True(restoredId is not null && folder.Has(restoredId),
            $"SERVER_SPEC.md § 10.10: the discarded file comes back into the folder. restoredId was '{restoredId}'.");
        Assert.Equal(discardedId, recovered.RequireLastAction("after the recovering undo").Id);
    }

    private sealed record Meta(int Matches, int Impressions, long LastPlayed, double Mu, double Sigma);

    private static async Task<Meta> ReadMetaAsync(Rm2Client client, string id)
    {
        var response = await client.MetaAsync(id);
        response.ShouldHaveStatus(200, $"GET /media/{id}/meta (SERVER_SPEC.md § 12.1)");

        var body = response.JsonBody;
        var rating = body.GetProperty("rating");
        return new Meta(
            body.GetProperty("matches").GetInt32(),
            body.GetProperty("impressions").GetInt32(),
            body.GetProperty("lastPlayed").GetInt64(),
            rating.GetProperty("mu").GetDouble(),
            rating.GetProperty("sigma").GetDouble());
    }
}
