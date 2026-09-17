using System.Text.Json;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>
/// SERVER_SPEC.md § 13.1, the bounded write-behind, and § 2.4's two keys.
///
/// <para>The promise this replaced was simple and strong: a 2xx meant the change was on disk. The
/// owner traded it (2026-09-17: <i>"I'm not afraid of losing a couple of votes, it's
/// non-consequential"</i>) because an fsync per vote is the dominant cost of a vote on a large
/// library. What replaced it is not a cache and must not become one, so every clause of the new
/// promise is a test here: the bound in time, the bound in count, the things that are never
/// deferred, the latch that stops choices piling up on a dead disk, and the switch that puts the old
/// behaviour back exactly.</para>
///
/// <para>Each class below names the mode it runs in, because the acceptance gate runs the whole
/// suite twice — once with the default <c>SaveDelaySeconds</c> and once with <c>0</c> — and a test
/// that inherited the ambient setting would assert something different on each run.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurabilityCollection :
    ICollectionFixture<SaveOnEveryChoiceServer>,
    ICollectionFixture<CountBoundServer>,
    ICollectionFixture<TimeBoundServer>
{
    public const string Name = "rm2-durability";
}

/// <summary>Reading the database the way the frozen desktop app would: filename → its counters.</summary>
public static class OnDisk
{
    public const string DatabaseName = "rankmaster_db.json";

    public static string PathOf(LibraryFolder folder) => folder.File(DatabaseName);

    public static bool Exists(LibraryFolder folder) => File.Exists(PathOf(folder));

    /// <summary>
    /// Total <c>matches</c> across every row on disk. A vote adds one to each of two records, so
    /// this counts 2 per vote — a single number that says how many votes the file knows about,
    /// without caring which pictures the selector happened to choose.
    /// </summary>
    public static int TotalMatches(LibraryFolder folder)
    {
        if (!Exists(folder))
            return 0;

        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(folder)));
        if (!document.RootElement.TryGetProperty("images", out var images))
            return 0;

        return images.EnumerateObject().Sum(row => row.Value.GetProperty("matches").GetInt32());
    }

    /// <summary>Total <c>impressions</c>: a vote adds 2, and so does a skip, which adds no matches.</summary>
    public static int TotalImpressions(LibraryFolder folder)
    {
        if (!Exists(folder))
            return 0;

        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(folder)));
        if (!document.RootElement.TryGetProperty("images", out var images))
            return 0;

        return images.EnumerateObject().Sum(row => row.Value.GetProperty("impressions").GetInt32());
    }

    public static IReadOnlyList<string> Keys(LibraryFolder folder)
    {
        if (!Exists(folder))
            return [];

        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(folder)));
        return document.RootElement.TryGetProperty("images", out var images)
            ? images.EnumerateObject().Select(p => p.Name).ToList()
            : [];
    }

    /// <summary>
    /// Makes every <c>JsonCatalog.Save</c> in this folder throw while leaving the folder writable,
    /// by parking a directory where the catalog needs to write <c>rankmaster_db.json.tmp</c>. The
    /// same lever the state-machine audit uses, and the closest thing here to the owner's drive
    /// going away mid-session.
    /// </summary>
    public static void JamSave(LibraryFolder folder) =>
        Directory.CreateDirectory(folder.File(DatabaseName + ".tmp"));

    public static void UnjamSave(LibraryFolder folder)
    {
        var jam = folder.File(DatabaseName + ".tmp");
        if (Directory.Exists(jam)) Directory.Delete(jam, recursive: true);
    }
}

/// <summary>
/// The bound measured on the clock: <c>SaveDelaySeconds</c> after the first unsaved choice, with
/// <c>MaxUnsavedChoices</c> set far out of reach so the count cannot be what writes.
/// </summary>
[Collection(DurabilityCollection.Name)]
public sealed class WriteBehindClockTests(TimeBoundServer server, ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync() => await server.ResetAsync();

    public async Task DisposeAsync() => await server.ResetAsync();

    /// <summary>
    /// § 13.1: "the database is written by the server, off the request path, no later than
    /// <c>SaveDelaySeconds</c> after the first unsaved choice". Both halves are asserted — that the
    /// vote's own 200 did <b>not</b> wait for the write (otherwise this is not a write-behind at
    /// all), and that the write lands without anyone asking for it.
    /// </summary>
    [Fact]
    public async Task A_vote_is_on_disk_within_the_bound()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        Assert.False(OnDisk.Exists(folder), "SERVER_SPEC.md § 10.1: Start() does not save.");

        var started = DateTimeOffset.UtcNow;
        var voted = (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.6: the vote is applied and answered");
        var answered = DateTimeOffset.UtcNow;

        Assert.Equal(1, voted.SessionVotes);

        // The response did not wait for the write. Only assertable while the bound has not yet
        // elapsed — on a loaded box the response itself can take longer than 200 ms, and a test that
        // fails because the machine was busy proves nothing about the server.
        if (answered - started < server.SaveDelay)
        {
            Assert.False(
                OnDisk.Exists(folder),
                "SERVER_SPEC.md § 13.1: a plain choice is applied in memory and answered at once — " +
                "\"the response does not wait for the write\" (§ 13.2). The database was already " +
                $"written {(answered - started).TotalMilliseconds:0} ms into a {server.SaveDelay.TotalMilliseconds:0} ms bound.");
        }

        // And now nobody asks for anything: the server's own clock writes it.
        var deadline = started + server.SaveDelay + TimeSpan.FromSeconds(10);
        while (OnDisk.TotalMatches(folder) < 2 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        var landed = DateTimeOffset.UtcNow;
        output.WriteLine($"bound {server.SaveDelay.TotalMilliseconds:0} ms; the vote reached disk " +
                         $"{(landed - started).TotalMilliseconds:0} ms after it was cast");

        Assert.True(
            OnDisk.TotalMatches(folder) == 2,
            "SERVER_SPEC.md § 13.1: the write-behind writes the database no later than " +
            $"SaveDelaySeconds after the first unsaved choice. {server.SaveDelay.TotalMilliseconds:0} ms " +
            "plus ten seconds of grace passed and the vote is still not on disk. That is not a bounded " +
            "write-behind; it is a cache.");
    }

    /// <summary>
    /// § 13.1: "While it is latched the flush keeps retrying every <c>SaveDelaySeconds</c>". So a
    /// drive that goes away and comes back does not need the owner to do anything: the choices that
    /// were stranded are written by the next tick that succeeds. Nobody sends a request here after
    /// the vote — the recovery is the server's own.
    /// </summary>
    [Fact]
    public async Task A_latched_flush_keeps_retrying_and_writes_when_the_disk_comes_back()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        OnDisk.JamSave(folder);

        try
        {
            (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, "applied in memory; the write is the server's problem (§ 13.1)");

            // Let at least one flush attempt fail and latch.
            await Task.Delay(server.SaveDelay + server.SaveDelay);
            Assert.False(OnDisk.Exists(folder), "the disk is not taking writes yet");

            OnDisk.UnjamSave(folder);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (OnDisk.TotalMatches(folder) < 2 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal(2, OnDisk.TotalMatches(folder));
            output.WriteLine("the latched flush retried on its own and wrote the stranded vote");
        }
        finally
        {
            OnDisk.UnjamSave(folder);
        }
    }
}

/// <summary>
/// The bound measured in choices, and everything that is never deferred. The clock is set to thirty
/// seconds, longer than any test in this class, so the only things that can write are the
/// <c>MaxUnsavedChoices</c>-th choice and the forced saves of § 13.1.
/// </summary>
[Collection(DurabilityCollection.Name)]
public sealed class WriteBehindBoundTests(CountBoundServer server, ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync() => await server.ResetAsync();

    public async Task DisposeAsync() => await server.ResetAsync();

    /// <summary>
    /// § 13.1: "or the <c>MaxUnsavedChoices</c>-th (default 5) unsaved choice, whichever first". The
    /// clock here is thirty seconds away, so the fifth choice is the only thing that can have
    /// written — and the four before it are the proof that the write was really deferred.
    /// </summary>
    [Fact]
    public async Task Five_choices_force_a_write()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        for (var i = 1; i < server.MaxUnsavedChoices; i++)
        {
            snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, $"vote {i}");

            Assert.False(
                OnDisk.Exists(folder),
                $"SERVER_SPEC.md § 13.1: with MaxUnsavedChoices = {server.MaxUnsavedChoices} and the " +
                $"clock {server.SaveDelay.TotalSeconds:0} s away, choice {i} must still be unwritten. " +
                "A write here means the batching is not batching.");
        }

        var fifth = (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, $"vote {server.MaxUnsavedChoices}");

        Assert.Equal(server.MaxUnsavedChoices, fifth.SessionVotes);

        Assert.True(
            OnDisk.Exists(folder),
            $"SERVER_SPEC.md § 13.1: the {server.MaxUnsavedChoices}th unsaved choice is written " +
            "synchronously, inside the request, before the response. Nothing was written.");

        Assert.Equal(2 * server.MaxUnsavedChoices, OnDisk.TotalMatches(folder));
        output.WriteLine(
            $"{server.MaxUnsavedChoices} votes coalesced into one write of {OnDisk.Keys(folder).Count} rows");
    }

    /// <summary>
    /// § 13.1's "What is never deferred", and the reason § 13.2's ordering survives: a discard
    /// flushes first, <b>then</b> moves the file, <b>then</b> writes the JSON. So the window in which
    /// the file is in <c>discarded/</c> and the JSON still lists it stays exactly what it was — one
    /// move wide — and the self-heal § 13.2 describes (the next <c>Scan</c> does not see the file, the
    /// next <c>Save</c> drops the row, ratings never lost) still holds, because the only row that can
    /// be dropped is the one whose file really did move.
    /// </summary>
    [Fact]
    public async Task A_move_forces_a_write_before_the_response()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var voted = (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "one vote, deferred");
        var skipped = (await client.SkipAsync(voted.RequireToken("ranking")))
            .ShouldBeSnapshot(200, "one skip, deferred");

        Assert.False(OnDisk.Exists(folder), "two choices, well inside both bounds, so nothing is written yet");

        var victim = skipped.Left.Id;
        var discarded = (await client.DiscardAsync(skipped.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.8: the discard moves the file");

        // The move's own 2xx still means "on disk now" (§ 13.1's narrow corollary).
        Assert.True(
            OnDisk.Exists(folder),
            "SERVER_SPEC.md § 13.1: \"the 2xx of every endpoint that moves a file means the change is " +
            "on disk now\". The discard answered 200 and there is no database.");

        Assert.Equal(2, OnDisk.TotalMatches(folder));
        Assert.Equal(4, OnDisk.TotalImpressions(folder));

        Assert.DoesNotContain(victim, OnDisk.Keys(folder));
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", victim)),
            "the file really is in discarded/");
        Assert.DoesNotContain(victim, discarded.PairIds);
        output.WriteLine($"discard of {victim} flushed the vote and the skip with it");
    }

    /// <summary>
    /// The other half of the same rule, and the one that keeps § 13.2's argument honest: if the
    /// forced write before a move cannot be made, the move does not happen at all. Nothing is
    /// applied, <c>pairSeq</c> does not move, the token stays current, and the file is where it was.
    ///
    /// <para>Moving a file on a disk that will not take the database is how a rating gets separated
    /// from its picture: the file would be in <c>discarded/</c> with a database that still describes
    /// the world several choices ago, and the self-heal of § 13.2 would then drop the wrong rows.</para>
    /// </summary>
    [Fact]
    public async Task A_move_whose_forced_write_cannot_be_made_moves_nothing()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var voted = (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "one unsaved choice");

        OnDisk.JamSave(folder);
        try
        {
            var token = voted.RequireToken("ranking");
            var victim = voted.Left.Id;

            var error = (await client.DiscardAsync(token, "left", "req-discard"))
                .ShouldBeError(
                    "save_failed",
                    "SERVER_SPEC.md § 13.1: the flush before a move is forced and synchronous; if it " +
                    "cannot be made the move is refused with nothing applied");

            Assert.False(error.Detail("fileMoved", "§ 8.3").GetBoolean());
            Assert.False(error.Detail("recordsChanged", "§ 8.3").GetBoolean());

            var after = error.RequireSession("§ 4");
            Assert.Equal(voted.PairSeq, after.PairSeq);
            Assert.Equal(token, after.PairToken);
            Assert.Contains(victim, after.PairIds);

            Assert.True(File.Exists(Path.Combine(folder.Path, victim)),
                "nothing was applied, so the file is still in the library folder");
            Assert.False(Directory.Exists(Path.Combine(folder.Path, "discarded")),
                "SERVER_SPEC.md § 13.2: the move never started, so discarded/ was never created");

            // § 8.3: the same token discards once the disk lets go.
            OnDisk.UnjamSave(folder);
            var discarded = (await client.DiscardAsync(token, "left", "req-discard"))
                .ShouldBeSnapshot(200, "§ 13.3: the identical request may be retried");
            Assert.Equal(voted.PairSeq + 1, discarded.PairSeq);
            Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", victim)));

            // And the vote that was waiting went to disk with it, in the right order: the flush
            // wrote it before the file moved, and the save after the move dropped the moved row.
            Assert.Equal(2, OnDisk.TotalMatches(folder));
            Assert.DoesNotContain(victim, OnDisk.Keys(folder));
        }
        finally
        {
            OnDisk.UnjamSave(folder);
        }
    }

    /// <summary>
    /// § 10.4 and § 13.1: a close is a durability point, and so is a clean shutdown. Both are the
    /// same flush; what differs is who calls it — the client, or <c>ApplicationStopping</c>.
    /// </summary>
    [Fact]
    public async Task Close_and_shutdown_flush()
    {
        var client = server.Client;

        // ---- DELETE /session (§ 10.4) ----------------------------------------------------------
        using (var folder = LibraryFolder.SixStills())
        {
            var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
            (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, "one vote, deferred");
            Assert.False(OnDisk.Exists(folder), "well inside both bounds");

            (await client.CloseSessionAsync()).ShouldHaveStatus(204, "SERVER_SPEC.md § 10.4: the close");

            Assert.Equal(2, OnDisk.TotalMatches(folder));
            Assert.True(OnDisk.Exists(folder),
                "SERVER_SPEC.md § 10.4: \"It flushes unsaved choices\" — no choice the owner made in " +
                "this session is lost by closing.");
        }

        // ---- shutdown (ApplicationStopping → SessionRegistry.Dispose) --------------------------
        using (var folder = LibraryFolder.SixStills())
        {
            var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
            var voted = (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, "one vote, deferred");
            (await client.SkipAsync(voted.RequireToken("ranking"))).ShouldBeSnapshot(200, "one skip, deferred");
            Assert.False(OnDisk.Exists(folder), "well inside both bounds");

            // Exactly what Rm2Host registers on ApplicationStopping.
            server.Registry.Dispose();

            Assert.Equal(2, OnDisk.TotalMatches(folder));
            Assert.Equal(4, OnDisk.TotalImpressions(folder));
            Assert.False(File.Exists(Path.Combine(folder.Path, ".rankmaster.lock")),
                "A3: the shutdown still deletes the lock file it always did.");
            output.WriteLine("shutdown wrote " + OnDisk.TotalImpressions(folder) + " impressions");
        }
    }

    /// <summary>
    /// § 13.1, "When a deferred write fails": the failure is latched, and every mutating call then
    /// attempts the write first and answers <c>500 save_failed</c> with <b>nothing applied</b> if it
    /// fails again. § 8.3's row holds exactly — <c>pairSeq</c> unchanged, the client's token still
    /// current, the identical request retryable. That is what stops the owner piling up choices on a
    /// drive that is not taking them.
    /// </summary>
    [Fact]
    public async Task A_failed_flush_latches_and_the_next_choice_reports_save_failed_with_nothing_applied()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        OnDisk.JamSave(folder);

        try
        {
            // Choices up to and including the one whose forced flush throws. None of them fails:
            // they are applied in memory and the write is the server's problem, not the client's.
            for (var i = 1; i <= server.MaxUnsavedChoices; i++)
            {
                snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
                    .ShouldBeSnapshot(200, $"choice {i} is applied in memory and answered (§ 13.1)");
            }

            var token = snapshot.RequireToken("ranking");
            var pairSeq = snapshot.PairSeq;
            var votes = snapshot.SessionVotes;

            var refused = await client.VoteAsync(token, "left", "the-next-choice");
            var error = refused.ShouldBeError(
                "save_failed",
                "SERVER_SPEC.md § 13.1: while a failed flush is latched, a mutating call attempts the " +
                "write first and answers 500 save_failed if it fails again");

            Assert.False(error.Detail("recordsChanged", "§ 8.3's vote/skip row").GetBoolean());
            Assert.False(error.Detail("fileMoved", "§ 8.3's vote/skip row").GetBoolean());

            var after = error.RequireSession("§ 4: a save_failed with a session open embeds the snapshot");
            Assert.Equal(pairSeq, after.PairSeq);
            Assert.Equal(token, after.PairToken);
            Assert.Equal(votes, after.SessionVotes);
            Assert.Equal(snapshot.PairIds, after.PairIds);

            output.WriteLine($"latched after {server.MaxUnsavedChoices} choices; the next one said save_failed");

            // § 8.3: "still valid — retry with the same token", once the disk comes back.
            OnDisk.UnjamSave(folder);
            var retried = (await client.VoteAsync(token, "left", "the-next-choice"))
                .ShouldBeSnapshot(200, "SERVER_SPEC.md § 8.3: the identical request may be retried");
            Assert.Equal(pairSeq + 1, retried.PairSeq);
            Assert.Equal(votes + 1, retried.SessionVotes);
        }
        finally
        {
            OnDisk.UnjamSave(folder);
        }
    }

    /// <summary>
    /// § 13.1: "A successful <c>POST /session/save</c> clears the latch", and § 10.5 names this the
    /// recovery path after the disk came back. Nothing is lost on the way: the choices that were
    /// unsaved while the latch was on are in the file the save writes.
    /// </summary>
    [Fact]
    public async Task Save_clears_the_latch()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        OnDisk.JamSave(folder);

        try
        {
            for (var i = 1; i <= server.MaxUnsavedChoices; i++)
            {
                snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
                    .ShouldBeSnapshot(200, $"choice {i}");
            }

            (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
                .ShouldBeError("save_failed", "the latch is on");

            // Still jammed: save says so, and says it the way § 10.5 says it.
            var stillJammed = (await client.SaveAsync())
                .ShouldBeError("save_failed", "SERVER_SPEC.md § 10.5: 500 save_failed on failure");
            Assert.False(stillJammed.Detail("recordsChanged", "§ 10.5").GetBoolean());

            OnDisk.UnjamSave(folder);

            var saved = (await client.SaveAsync())
                .ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.5: the save that clears the latch");
            Assert.NotNull(saved.LastSavedAt);

            Assert.Equal(2 * server.MaxUnsavedChoices, OnDisk.TotalMatches(folder));

            // The latch is gone: the next choice is served normally again.
            var voted = (await client.VoteAsync(saved.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, "SERVER_SPEC.md § 13.1: the latch is cleared, so this is a plain choice");
            Assert.Equal(server.MaxUnsavedChoices + 1, voted.SessionVotes);
        }
        finally
        {
            OnDisk.UnjamSave(folder);
        }
    }

    /// <summary>
    /// § 13.3 against the batched server, and the one thing § 1.3 of the remediation plan asked to be
    /// proved rather than argued: the retry rule is about in-memory state and the write-behind cannot
    /// disturb it.
    ///
    /// <para>The sequence is the real one: the client votes, the response is lost, the server's flush
    /// lands the write, and the client — still holding the only token it ever had — resends. One
    /// vote, not two, and the file agrees.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_response_then_a_flush_then_a_resend_applies_exactly_one_vote()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var token = opened.RequireToken("ranking");
        var left = opened.Left.Id;
        var right = opened.Right.Id;

        // The vote lands. Imagine the client never saw this.
        var landed = (await client.VoteAsync(token, "left", "one-logical-vote"))
            .ShouldBeSnapshot(200, "the vote the client does not know about");
        Assert.Equal(1, landed.SessionVotes);

        // The write-behind writes it, off the request path. POST /session/save is the contract's own
        // way of making that moment definite (§ 10.5), which is precisely what a client that lost a
        // response cannot do — but the test can, and it puts the flush exactly between the two
        // attempts, which is the ordering § 1.3 asks about.
        (await client.SaveAsync()).ShouldBeSnapshot(200, "the flush lands between the two attempts");
        Assert.Equal(2, OnDisk.TotalMatches(folder));

        // The client resends the same token, as § 13.3 requires ("yes, with the same pairToken").
        var replay = await client.VoteAsync(token, "left", "one-logical-vote");
        var error = replay.ShouldBeError(
            "stale_pair_token",
            "SERVER_SPEC.md § 13.3: a token is consumed exactly once. pairSeq advances when the engine " +
            "enters the action, not when the write lands, so a write-behind cannot make a retry a " +
            "second vote.");

        var resync = error.RequireSession("§ 8.5: one response fully resynchronises the client");
        Assert.Equal(landed.PairSeq, resync.PairSeq);
        Assert.Equal(1, resync.SessionVotes);

        // And the counters — the fact, rather than the status code.
        (await client.SaveAsync()).ShouldBeSnapshot(200, "make the final state durable before reading it");
        Assert.Equal(2, OnDisk.TotalMatches(folder));

        using var document = JsonDocument.Parse(File.ReadAllText(OnDisk.PathOf(folder)));
        var images = document.RootElement.GetProperty("images");
        foreach (var id in new[] { left, right })
        {
            Assert.True(images.TryGetProperty(id, out var row), $"'{id}' is a row in the database");
            Assert.Equal(1, row.GetProperty("matches").GetInt32());
            Assert.Equal(1, row.GetProperty("impressions").GetInt32());
        }

        output.WriteLine("lost response → flush → resend: one vote, matches 1 on both records");
    }
}

/// <summary>
/// <c>SaveDelaySeconds = 0</c>. § 13.1: "restores the old behaviour exactly — every choice saved
/// before its response, the invariant as it was written above the line". This is the escape hatch,
/// and an escape hatch that only nearly works is worse than none.
/// </summary>
[Collection(DurabilityCollection.Name)]
public sealed class SaveOnEveryChoiceTests(SaveOnEveryChoiceServer server, ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync() => await server.ResetAsync();

    public async Task DisposeAsync() => await server.ResetAsync();

    [Fact]
    public async Task SaveDelaySeconds_zero_restores_save_on_every_choice()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        Assert.False(OnDisk.Exists(folder), "SERVER_SPEC.md § 10.1: Start() still does not save.");

        // Every choice, on disk before its own response — checked between each one, with nothing
        // waited for and nothing asked for.
        for (var i = 1; i <= 4; i++)
        {
            snapshot = (await client.VoteAsync(snapshot.RequireToken("ranking"), "left"))
                .ShouldBeSnapshot(200, $"vote {i}");

            Assert.Equal(2 * i, OnDisk.TotalMatches(folder));
            Assert.NotNull(snapshot.LastSavedAt);
        }

        snapshot = (await client.SkipAsync(snapshot.RequireToken("ranking")))
            .ShouldBeSnapshot(200, "a skip is a choice too (§ 10.7)");
        Assert.Equal(8, OnDisk.TotalMatches(folder));
        Assert.Equal(10, OnDisk.TotalImpressions(folder));

        // And the cancel of a choice, which is the third thing § 13.1 calls a plain choice.
        (await client.UndoAsync()).ShouldBeSnapshot(200, "the cancel of the skip (§ 10.10)");
        Assert.Equal(8, OnDisk.TotalImpressions(folder));

        output.WriteLine("SaveDelaySeconds = 0: every choice was on disk before its response");
    }

    /// <summary>
    /// The other half of "exactly": with the switch at 0 a choice whose write throws is rolled back
    /// whole and answered <c>500 save_failed</c> with the token still current — SERVER_SPEC.md § 8.3's
    /// first row, reached inside the vote itself rather than through the latch.
    /// </summary>
    [Fact]
    public async Task With_the_switch_at_zero_a_vote_whose_save_throws_is_rolled_back_in_the_same_request()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var token = opened.RequireToken("ranking");

        OnDisk.JamSave(folder);
        try
        {
            var error = (await client.VoteAsync(token, "left", "req-1"))
                .ShouldBeError("save_failed", "SERVER_SPEC.md § 10.6: the first vote fails, not the sixth");

            Assert.False(error.Detail("recordsChanged", "§ 10.6").GetBoolean());
            var after = error.RequireSession("§ 4");
            Assert.Equal(opened.PairSeq, after.PairSeq);
            Assert.Equal(token, after.PairToken);
            Assert.Equal(0, after.SessionVotes);
            Assert.Empty(after.Cues);
        }
        finally
        {
            OnDisk.UnjamSave(folder);
        }

        var ok = (await client.VoteAsync(token, "left", "req-1"))
            .ShouldBeSnapshot(200, "§ 8.3: the identical request may be retried");
        Assert.Equal(1, ok.SessionVotes);
        Assert.Equal(2, OnDisk.TotalMatches(folder));
    }
}
