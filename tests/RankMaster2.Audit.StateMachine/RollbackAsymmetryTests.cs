using RankMaster2.Audit.StateMachine.Harness;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.StateMachine;

/// <summary>
/// SERVER_SPEC.md § 8.3, the whole table, driven by a save that really throws.
///
/// Vote and skip save *inside* the library call and roll the in-memory state back when that save
/// throws, so their failure changes nothing and the client's token stays current. Discard, special
/// and undo move the file *first* and then call <c>Drop</c>/<c>Restore</c>, neither of which saves
/// or can roll back, so a save that throws after the move leaves the change committed and
/// <c>pairSeq</c> advanced. Same status code, opposite meaning — which makes the details field and
/// the embedded snapshot the only things standing between the client and a wrong recovery.
///
/// The lever is a directory sitting on <c>rankmaster_db.json.tmp</c>: the catalog writes that file
/// first, so the save fails while the folder stays writable and file moves still work. Nothing here
/// mocks the catalog.
/// </summary>
public sealed class RollbackAsymmetryTests(Rm2Server server) : AuditTestBase(server)
{
    // ---- vote and skip: full rollback, token still valid ---------------------------------------

    [Fact]
    public async Task A_vote_whose_save_throws_changes_nothing_and_the_same_token_still_votes()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");
        var ids = opened.PairIds;

        folder.JamSave();
        var failed = await client.VoteAsync(token, "left", "req-1");
        var error = failed.ShouldBeError(
            "save_failed",
            "SERVER_SPEC.md § 10.6: a vote whose save throws answers 500 save_failed");

        Assert.False(
            error.Detail("recordsChanged", "§ 10.6 requires details.recordsChanged on a failed vote").GetBoolean(),
            "SERVER_SPEC.md § 10.6: a rolled-back vote reports recordsChanged: false.");

        var after = error.RequireSession("§ 4: a save_failed with a session open embeds the snapshot");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(token, after.PairToken);
        Assert.Equal(0, after.SessionVotes);
        Assert.Empty(after.Cues);
        Assert.Equal(ids, after.PairIds);
        Assert.Null(after.LastAction);

        // § 8.3: "still valid — retry with the same token".
        folder.UnjamSave();
        var retried = await client.VoteAsync(token, "left", "req-1");
        var ok = retried.ShouldBeSnapshot(200, "§ 8.3: the identical request may be retried after a rolled-back vote");
        Assert.Equal(opened.PairSeq + 1, ok.PairSeq);
        Assert.Equal(1, ok.SessionVotes);
        Assert.Single(ok.Cues);
    }

    [Fact]
    public async Task A_skip_whose_save_throws_changes_nothing_and_the_same_token_still_skips()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");

        folder.JamSave();
        var failed = await client.SkipAsync(token, "req-skip");
        var error = failed.ShouldBeError("save_failed", "SERVER_SPEC.md § 10.7: skip fails exactly as vote does");
        var after = error.RequireSession("§ 4");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(token, after.PairToken);
        Assert.Null(after.LastAction);

        folder.UnjamSave();
        var ok = (await client.SkipAsync(token, "req-skip"))
            .ShouldBeSnapshot(200, "§ 8.3: the token survives a rolled-back skip");
        Assert.Equal(opened.PairSeq + 1, ok.PairSeq);
        Assert.Equal(0, ok.SessionVotes);   // § 9.1: skip is not a vote
    }

    /// <summary>
    /// The exact shape of the bug this project already fixed once: the folder still exists but lists
    /// no media, so <c>JsonCatalog.Save</c> must throw rather than write <c>{}</c> over real ratings
    /// (SPEC.md § Persistence, "Never write an empty database"). The vote must roll back whole.
    /// </summary>
    [Fact]
    public async Task A_vote_after_every_media_file_vanished_refuses_to_write_an_empty_database()
    {
        using var folder = AuditFolder.TwoStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");

        var alpha = folder.Bytes("alpha.jpg");
        var bravo = folder.Bytes("bravo.jpg");
        folder.Delete("alpha.jpg").Delete("bravo.jpg");

        var failed = await client.VoteAsync(token, "left", "req-empty");
        var error = failed.ShouldBeError(
            "save_failed",
            "SPEC.md § Persistence: a folder that lists no media while the session holds records must not be saved");
        var after = error.RequireSession("§ 4");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(token, after.PairToken);
        Assert.Equal(0, after.SessionVotes);

        folder.Write("alpha.jpg", alpha).Write("bravo.jpg", bravo);
        var ok = (await client.VoteAsync(token, "left", "req-empty"))
            .ShouldBeSnapshot(200, "§ 8.3: one call recovers once the library is back");
        Assert.Equal(1, ok.SessionVotes);
    }

    // ---- discard: move first, so a failed save is committed ------------------------------------

    [Fact]
    public async Task A_discard_whose_move_throws_changes_nothing_and_the_same_token_still_discards()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");
        var victim = opened.Left.Id;

        folder.JamDiscardFolder();
        var failed = await client.DiscardAsync(token, "left", "req-move");
        var error = failed.ShouldBeError(
            "move_failed",
            "SERVER_SPEC.md § 10.8: a discard whose move throws is 500 move_failed");
        Assert.Equal("move", error.Detail("stage", "§ 10.8 requires details.stage").GetString());

        var after = error.RequireSession("§ 4");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(token, after.PairToken);
        Assert.False(after.UndoAvailable, "§ 8.3: a move that threw changed nothing, so there is nothing to undo.");
        Assert.True(folder.Has(victim), "§ 8.3: nothing changed, so the file is still where it was.");

        folder.UnjamDiscardFolder();
        var ok = (await client.DiscardAsync(token, "left", "req-move"))
            .ShouldBeSnapshot(200, "§ 13.3: the same token recovers a discard whose move failed");
        Assert.Equal(opened.PairSeq + 1, ok.PairSeq);
        Assert.False(folder.Has(victim));
        Assert.True(File.Exists(folder.Discarded(victim)));
    }

    [Fact]
    public async Task A_discard_whose_save_throws_is_committed_and_says_so()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");
        var victim = opened.Left.Id;

        // Start() does not save (§ 10.1), so give the folder a real database first: the recovery
        // this test checks is the one § 10.8 describes, where the JSON still lists the moved file.
        (await client.SaveAsync()).ShouldBeSnapshot(200, "§ 10.5: an explicit save writes the database");

        folder.JamSave();
        var failed = await client.DiscardAsync(token, "left", "req-discard");
        var error = failed.ShouldBeError(
            "save_failed",
            "SERVER_SPEC.md § 10.8: move ok, save throws is 500 save_failed, not move_failed");
        Assert.True(error.Detail("fileMoved", "§ 10.8 requires details.fileMoved").GetBoolean());
        Assert.True(error.Detail("recordsChanged", "§ 10.8 requires details.recordsChanged").GetBoolean());

        var after = error.RequireSession("§ 4");
        Assert.Equal(opened.PairSeq + 1, after.PairSeq);
        Assert.NotEqual(token, after.PairToken);
        Assert.DoesNotContain(victim, after.PairIds);
        Assert.Equal(5, after.Total);
        Assert.True(after.UndoAvailable, "§ 10.8: the file is in discarded/, so undo is the way back.");

        var last = after.RequireLastAction("§ 9.4: a committed discard is the session's last action");
        Assert.Equal("discard", last.Type);
        Assert.Equal(victim, last.Id);
        Assert.Equal("left", last.SideValue);
        Assert.Equal(token, last.PairToken);
        Assert.Equal("req-discard", last.ClientRequestId);
        Assert.Equal(after.PairSeq, last.Seq);

        Assert.False(folder.Has(victim));
        Assert.True(File.Exists(folder.Discarded(victim)));

        // § 10.8: "the JSON still lists it, and the next successful save will drop it".
        Assert.Contains(victim, File.ReadAllText(folder.DatabasePath));
        folder.UnjamSave();
        (await client.SaveAsync()).ShouldBeSnapshot(200, "§ 10.5: save is always safe to retry");
        Assert.DoesNotContain(victim, File.ReadAllText(folder.DatabasePath));
    }

    [Fact]
    public async Task A_special_whose_save_throws_is_committed_to_special_1_and_says_so()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var token = opened.RequireToken("a fresh session is ranking");
        var victim = opened.Right.Id;

        folder.JamSave();
        var error = (await client.SpecialAsync(token, "right", "req-special"))
            .ShouldBeError("save_failed", "SERVER_SPEC.md § 10.9 is § 10.8 but for special 1/");
        var after = error.RequireSession("§ 4");

        Assert.Equal(opened.PairSeq + 1, after.PairSeq);
        Assert.Equal("special", after.RequireLastAction("§ 9.4").Type);
        Assert.True(File.Exists(folder.Special(victim)));
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "special 2")),
            "SPEC.md § File actions: v1 only ever creates 'special 1'.");
    }

    // ---- undo ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_undo_whose_save_throws_is_committed_and_says_so()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var victim = opened.Left.Id;

        var discarded = (await client.DiscardAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "a clean discard");
        Assert.True(discarded.UndoAvailable);

        folder.JamSave();
        var error = (await client.UndoAsync("req-undo"))
            .ShouldBeError("save_failed", "SERVER_SPEC.md § 8.3: undo move-back ok, save throws is a committed save_failed");
        Assert.True(error.Detail("fileMoved", "§ 8.3").GetBoolean());
        Assert.True(error.Detail("recordsChanged", "§ 8.3").GetBoolean());

        var after = error.RequireSession("§ 4");
        Assert.Equal(discarded.PairSeq + 1, after.PairSeq);
        Assert.False(after.UndoAvailable, "§ 10.10: the move has been reversed, so it must not be offered again.");
        var last = after.RequireLastAction("§ 9.4");
        Assert.Equal("undo", last.Type);
        Assert.Equal(victim, last.Id);
        Assert.Equal(victim, last.RestoredId);
        Assert.True(folder.Has(victim), "the file really is back in the library folder.");
        Assert.False(File.Exists(folder.Discarded(victim)));
    }

    /// <summary>
    /// The move back fails because the discarded file is no longer in <c>discarded/</c> — the single
    /// likeliest thing a person does to that folder. SERVER_SPEC.md § 8.3 has one row for this:
    ///
    ///   undo: move-back throws | nothing changed | pairSeq unchanged | token still valid
    ///
    /// So the server must answer <c>500 move_failed</c> with <c>stage: "undo-move"</c>, leave
    /// <c>pairSeq</c> alone, and keep <c>undoAvailable</c> true. It must not report a committed undo,
    /// and it must not claim in <c>lastAction.restoredId</c> that a file came back.
    /// </summary>
    [Fact]
    public async Task An_undo_whose_move_back_throws_must_change_nothing_and_must_not_claim_a_restore()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var victim = opened.Left.Id;

        var discarded = (await client.DiscardAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "a clean discard");
        Assert.True(discarded.UndoAvailable);

        // Somebody emptied discarded/ from the file manager.
        File.Delete(folder.Discarded(victim));

        var response = await client.UndoAsync("req-undo-lost");
        var error = response.ShouldBeError(
            "move_failed",
            "SERVER_SPEC.md § 8.3, row 'undo: move-back throws': nothing changed, so this is move_failed, " +
            "not a committed save_failed. The file never came back — the record was never restored — and " +
            "reporting the committed branch tells the client its library is whole when it is not.");
        Assert.Equal("undo-move", error.Detail("stage", "§ 8.3 / § 10.10").GetString());

        var after = error.RequireSession("§ 4");
        Assert.Equal(discarded.PairSeq, after.PairSeq);
        Assert.Equal(
            discarded.PairToken,
            after.PairToken);
        Assert.Equal(discarded.Total, after.Total);
        Assert.DoesNotContain(victim, after.PairIds);

        Assert.True(
            after.UndoAvailable,
            "SERVER_SPEC.md § 8.3: the move back threw, so the move is still recorded and undo is " +
            "still the way back. Clearing it here is what makes the loss permanent.");

        var last = after.LastAction;
        if (last is not null)
        {
            Assert.False(
                last.Type == "undo",
                "SERVER_SPEC.md § 9.4: lastAction records the most recent *successful* mutation. " +
                $"An undo that could not move the file back is not one, and lastAction.restoredId " +
                $"('{last.RestoredId}') names a file that is neither on disk nor a record.");
        }
    }

    /// <summary>
    /// The same fault, stated as the thing the client can measure: the response says
    /// <c>recordsChanged: true</c> and <c>fileMoved: true</c>, and the snapshot in the same envelope
    /// says the record count did not change and the id is not a record. One of those is a lie, and a
    /// client that believes the details field stops looking for the file.
    /// </summary>
    [Fact]
    public async Task A_lost_undo_must_not_report_a_committed_change_that_its_own_snapshot_contradicts()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var victim = opened.Left.Id;

        var discarded = (await client.DiscardAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "a clean discard");

        File.Delete(folder.Discarded(victim));

        var response = await client.UndoAsync("req-undo-lost");
        var error = response.ShouldBeErrorOneOf("§ 8.3 / § 10.10", "move_failed", "save_failed");
        if (error.Code == "move_failed")
            return;   // the contract's answer; nothing to contradict.

        var after = error.RequireSession("§ 4");
        var recordsChanged = error.Detail("recordsChanged", "§ 8.3").GetBoolean();

        Assert.False(
            recordsChanged && after.Total == discarded.Total,
            $"The 500 says recordsChanged: true, but counts.total is still {after.Total} — the same as " +
            "before the undo. Nothing was restored. SERVER_SPEC.md § 8.3 reserves this branch for " +
            "'move-back ok, Restore ok, Save throws', where the record is back in memory.");

        Assert.True(
            folder.Has(victim) || File.Exists(folder.Discarded(victim)),
            $"The 500 says fileMoved: true, but '{victim}' is in neither the library folder nor " +
            "discarded/. The server reported a move that did not happen.");
    }

    [Fact]
    public async Task Undo_with_nothing_recorded_is_a_409_that_still_carries_the_snapshot()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var error = (await client.UndoAsync("req-nothing"))
            .ShouldBeError("nothing_to_undo", "SERVER_SPEC.md § 10.10: no recorded move is 409 nothing_to_undo");
        var after = error.RequireSession("§ 10.10 requires error.session on this 409");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Null(after.LastAction);
    }

    /// <summary>§ 13.3: "a second undo is 409 nothing_to_undo; it cannot undo two moves".</summary>
    [Fact]
    public async Task A_second_undo_cannot_reach_back_past_the_one_recorded_move()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var first = opened.Left.Id;
        var afterFirst = (await client.DiscardAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "first discard");
        var second = afterFirst.Left.Id;
        var afterSecond = (await client.DiscardAsync(afterFirst.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "second discard");

        var undone = (await client.UndoAsync()).ShouldBeSnapshot(200, "§ 10.10: undo the last move");
        Assert.False(undone.UndoAvailable);
        Assert.True(folder.Has(second));
        Assert.False(folder.Has(first), "§ 10.10: one level, no stack — the earlier move stays.");

        (await client.UndoAsync()).ShouldBeError(
            "nothing_to_undo", "§ 13.3: a second undo cannot undo two moves");
        Assert.True(File.Exists(folder.Discarded(first)));
    }
}
