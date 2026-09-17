using RankMaster2.Catalog;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// Opening, resuming, reading and closing the one session (SERVER_SPEC.md § 10.1–§ 10.4).
///
/// The rule that matters most here is the quiet one in § 10.1: re-opening the folder already open is
/// a *pure read*. `Start()` clears the cues, the session vote count and the recent-shown set, so a
/// server that calls it again silently throws away the state of a phone that was merely resuming.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class SessionLifecycleTests(Rm2Server server) : SessionTestBase(server)
{
    [Fact]
    public async Task Opening_a_rankable_folder_returns_a_created_session_and_its_location()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var response = await client.OpenSessionAsync(folder.Path);
        var snapshot = response.ShouldBeSnapshot(201,
            "SERVER_SPEC.md § 10.1: a folder that opens answers 201 with the first pair picked");

        var location = response.HeaderOrNull("Location");
        Assert.True(location is not null && location.EndsWith("/api/v1/session", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 10.1: a 201 carries Location: /api/v1/session. Got '{location ?? "(absent)"}'.");

        Assert.Equal("ranking", snapshot.State);
        Assert.Equal(0, snapshot.PairSeq);
        Assert.Equal(0, snapshot.SessionVotes);
        Assert.Empty(snapshot.Cues);
        Assert.Null(snapshot.LastAction);
        Assert.Equal("still", snapshot.Policy);
        Assert.Equal(6, snapshot.Total);

        // § 10.1: "Start() does not save. A freshly opened session has lastSavedAt: null even though
        // rankmaster_db.json may be older — that field describes this session's writes."
        Assert.True(snapshot.LastSavedAt is null,
            $"SERVER_SPEC.md § 10.1: a freshly opened session has lastSavedAt null, got '{snapshot.LastSavedAt}'.");

        Assert.False(File.Exists(folder.File("rankmaster_db.json")),
            "SERVER_SPEC.md § 13.2: POST /session reads the JSON and writes nothing.");
    }

    [Fact]
    public async Task The_resolved_folder_path_comes_back_absolute()
    {
        using var folder = LibraryFolder.SixStills();
        var snapshot = await OpenAsync(folder);

        Assert.Equal(Path.GetFullPath(folder.Path), snapshot.Folder);
        Assert.Equal(new DirectoryInfo(folder.Path).Name, snapshot.FolderName);
    }

    [Fact]
    public async Task Reopening_the_same_folder_resumes_without_disturbing_anything()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        // Build up some session state first — a vote gives us a cue and a vote count, which are
        // exactly what a second Start() would silently wipe.
        var vote = await client.VoteAsync(opened.RequireToken("open"), "left", "resume-setup");
        var afterVote = vote.ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.6: a vote returns the new snapshot");

        var resumed = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.1: POST /session naming the folder already open is a 200 and a pure read");

        Assert.Equal(afterVote.SessionId, resumed.SessionId);
        Assert.Equal(afterVote.PairSeq, resumed.PairSeq);
        Assert.Equal(afterVote.SessionVotes, resumed.SessionVotes);
        Assert.Equal(afterVote.PairToken, resumed.PairToken);
        Assert.Equal(afterVote.Cues, resumed.Cues);

        Assert.True(resumed.SessionVotes == 1,
            "SERVER_SPEC.md § 10.1: resuming must not call Start() again — doing so clears cues, " +
            $"SessionVotes and the recent-shown set. sessionVotes came back as {resumed.SessionVotes}, not 1.");
        Assert.True(resumed.Cues.Length == 1,
            $"SERVER_SPEC.md § 10.1: the cue from the vote survives a resume; got {resumed.Cues.Length} cues.");
    }

    [Fact]
    public async Task Opening_a_second_folder_is_refused_rather_than_closing_the_first()
    {
        using var first = LibraryFolder.SixStills();
        using var second = LibraryFolder.TwoStills();

        var opened = await OpenAsync(first);
        var client = await ClientAsync();

        var response = await client.OpenSessionAsync(second.Path);
        var failure = response.ShouldBeError("session_already_open",
            "SERVER_SPEC.md § 10.1: a different folder while one is open is 409; the server never closes " +
            "the open session implicitly.");

        Assert.Equal(Path.GetFullPath(first.Path),
                     failure.Detail("openFolder", "session_already_open details").GetString());

        var still = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "the first session is untouched");
        Assert.Equal(opened.SessionId, still.SessionId);
        Assert.Equal(Path.GetFullPath(first.Path), still.Folder);
    }

    [Fact]
    public async Task A_folder_with_one_file_never_opens()
    {
        using var folder = LibraryFolder.OneStill();
        var client = await ClientAsync();

        var response = await client.OpenSessionAsync(folder.Path);
        var failure = response.ShouldBeError("folder_not_rankable",
            "SERVER_SPEC.md § 10.1: Start() returning false — fewer than two eligible files — is 409.");

        Assert.Equal(1, failure.Detail("stills", "folder_not_rankable details").GetInt32());
        Assert.Equal(0, failure.Detail("videos", "folder_not_rankable details").GetInt32());
        Assert.Equal(1, failure.Detail("rankable", "folder_not_rankable details").GetInt32());

        // § 10.1 step 5: the lock is released before the response, and the session is discarded.
        Assert.False(File.Exists(folder.File(".rankmaster.lock")),
            "SERVER_SPEC.md § 10.1: the folder lock is released before a folder_not_rankable is returned.");

        var session = await client.GetSessionAsync();
        session.ShouldBeError("no_session",
            "SERVER_SPEC.md § 7.1: an unrankable folder leaves the server with no session at all");
    }

    [Fact]
    public async Task An_empty_folder_is_unrankable_too()
    {
        using var folder = LibraryFolder.Empty();
        var client = await ClientAsync();

        var failure = (await client.OpenSessionAsync(folder.Path)).ShouldBeError("folder_not_rankable",
            "SERVER_SPEC.md § 10.1: a folder with no media has nothing to rank");

        Assert.Equal(0, failure.Detail("rankable", "folder_not_rankable details").GetInt32());
    }

    [Fact]
    public async Task An_unreadable_library_file_refuses_the_open_and_is_never_overwritten()
    {
        using var folder = LibraryFolder.WithUnreadableDatabase();
        var databasePath = folder.File("rankmaster_db.json");
        var before = await File.ReadAllBytesAsync(databasePath);

        var client = await ClientAsync();
        var failure = (await client.OpenSessionAsync(folder.Path)).ShouldBeError("library_json_unreadable",
            "SERVER_SPEC.md § 10.1: JsonCatalog.LoadRequired throwing is 409 library_json_unreadable");

        Assert.Equal(databasePath, failure.Detail("file", "library_json_unreadable details").GetString());

        // The strongest clause in § 10.1: the file "MUST NOT be written on this path, at any later
        // point in the request, or during cleanup". Someone's ratings are in there.
        var after = await File.ReadAllBytesAsync(databasePath);
        Assert.True(before.SequenceEqual(after),
            "SERVER_SPEC.md § 10.1 and SPEC.md § Persistence: an unparseable rankmaster_db.json MUST NOT be " +
            "overwritten — not on this path, not later in the request, not during cleanup. The file changed.");

        Assert.False(File.Exists(folder.File(".rankmaster.lock")),
            "SERVER_SPEC.md § 10.1: the lock is released before library_json_unreadable is returned.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("./also-relative")]
    public async Task A_folder_that_is_not_an_absolute_path_is_refused(string folder)
    {
        var client = await ClientAsync();
        var response = await client.OpenSessionAsync(folder);

        response.ShouldBeErrorOneOf(
            $"SERVER_SPEC.md § 10.1: 'folder' must be non-empty and rooted; '{folder}' is not",
            "invalid_path", "missing_field");
    }

    [Fact]
    public async Task A_folder_containing_a_nul_is_refused()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{\"folder\":\"/tmp/a\\u0000b\"}");

        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 5.2: a path containing a NUL is 400 invalid_path",
            "invalid_path", "invalid_request");
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_a_not_found()
    {
        var client = await ClientAsync();
        var missing = Path.Combine(Path.GetTempPath(), $"rm2-absent-{Guid.NewGuid():N}");

        var failure = (await client.OpenSessionAsync(missing)).ShouldBeError("folder_not_found",
            "SERVER_SPEC.md § 10.1 step 3: a folder that does not exist is 404 folder_not_found");

        Assert.Equal(missing, failure.Detail("folder", "folder_not_found details").GetString());
    }

    [Fact]
    public async Task A_path_that_is_a_file_is_not_a_directory()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var failure = (await client.OpenSessionAsync(folder.File(MediaFixtures.PlainJpeg)))
            .ShouldBeError("folder_not_a_directory",
                "SERVER_SPEC.md § 10.1 step 3: a path that exists but is a file is 400 folder_not_a_directory");

        Assert.NotNull(failure.Detail("folder", "folder_not_a_directory details").GetString());
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.1 step 4 and § 5.3.1. Holding the folder's lock file from outside is what
    /// a second Rank Master process would do, and the server must refuse the folder rather than
    /// writing into it alongside.
    ///
    /// <para>The lock is held by a <b>second process</b> (<c>tests/RankMaster2.LockHolder</c>), which
    /// is the only way to ask the question the contract asks. This test used to open the file from
    /// the test process itself: .NET enforces <c>FileShare.None</c> in-process too, so it passed
    /// while proving nothing about a different program — and it carried an escape hatch that turned a
    /// genuine failure into a printed remark about advisory locking. On Linux .NET maps
    /// <c>FileShare.None</c> onto <c>flock(2)</c>, which is cross-process, so the 423 is real here
    /// and the hatch was never earned (G-audit-remediation § 2, T1b).</para>
    ///
    /// <para><c>details.holder</c> is null by construction, and the spec spends a section explaining
    /// why: the lock is opened <c>FileShare.None</c>, which is exactly what stops a second writer —
    /// and that same exclusivity stops anyone opening the file to read who holds it.</para>
    /// </summary>
    [Fact]
    public async Task A_folder_whose_lock_is_held_by_another_process_is_refused()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        using var holder = LockHolderProcess.Hold(folder.Path);

        var response = await client.OpenSessionAsync(folder.Path);

        if (response.StatusCode is 200 or 201)
        {
            await client.CloseSessionAsync();
            throw new Xunit.Sdk.XunitException(
                "SERVER_SPEC.md § 10.1 step 4: opening a folder whose .rankmaster.lock is held by another " +
                "process must be 423 folder_locked, and this opened it instead.\n" + response.Describe());
        }

        var failure = response.ShouldBeError("folder_locked",
            "SERVER_SPEC.md § 10.1 step 4: a folder whose lock is held elsewhere is 423 folder_locked");

        var holderDetail = failure.Detail("holder", "folder_locked details");
        Assert.True(holderDetail.ValueKind == System.Text.Json.JsonValueKind.Null,
            "SERVER_SPEC.md § 5.3.1: details.holder is null, always. The lock is FileShare.None, so no " +
            "other process can open it to read the holder record — a best-effort read cannot succeed and " +
            $"would only produce a misleading error path. Got {holderDetail}.");

        Assert.NotNull(response.HeaderOrNull("X-Request-Id"));

        // And the moment the other process lets go, the folder opens.
        holder.Release();
        (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(
            201, "the folder opens once the other process has released the lock");
    }

    /// <summary>
    /// AUDIT.md H11, the cheap half. Browsing the whole filesystem is the product decision and stays;
    /// what does not stay is <c>POST /session</c> — which can discard files into subfolders and
    /// rename every file in the folder — accepting path forms that the read-only
    /// <c>GET /libraries/browse</c> refuses. One guard, one answer.
    /// </summary>
    [Theory]
    [InlineData("/tmp/../etc", "a relative segment")]
    [InlineData("/tmp/./photos", "a dot segment")]
    [InlineData("\\\\evil\\share", "a UNC path — on Windows this makes the server authenticate to a host of the caller's choosing")]
    [InlineData("\\\\?\\C:\\photos", "the Win32 device namespace, which turns off the normalisation every check depends on")]
    [InlineData("/tmp/NUL", "a reserved device name")]
    [InlineData("/tmp/photos.", "a segment ending in a dot, which Windows silently strips")]
    public async Task Session_open_refuses_what_browse_refuses(string path, string because)
    {
        var client = await ClientAsync();

        var browse = await client.BrowseAsync(path);
        var open = await client.OpenSessionAsync(path);

        browse.ShouldBeError("invalid_path", $"GET /libraries/browse refuses {because}");
        open.ShouldBeError("invalid_path",
            $"SERVER_SPEC.md § 10.1 step 1 and AUDIT.md H11: POST /session takes the same path guard as " +
            $"/libraries/browse, and this is {because}. The endpoint that moves files must not be the " +
            "lenient one.");
    }

    /// <summary>
    /// AUDIT.md A9. <c>JsonCatalog.Save</c> writes a temp file and replaces atomically; a crash
    /// between the two leaves <c>rankmaster_db.json.tmp</c> beside the photographs. It is skipped by
    /// every scan and overwritten by the next save, so it costs nothing except the owner wondering
    /// what it is. Open is the one moment the server knows no save is in flight in this folder.
    /// </summary>
    [Fact]
    public async Task A_stale_tmp_is_removed_on_open()
    {
        using var folder = LibraryFolder.SixStills();
        var stale = folder.File("rankmaster_db.json.tmp");
        await File.WriteAllTextAsync(stale, "{ half-written, from a crash }");

        var snapshot = await OpenAsync(folder);

        Assert.False(File.Exists(stale),
            "SERVER_SPEC.md § 13.2 / AUDIT.md A9: a stale rankmaster_db.json.tmp is cleaned up when the " +
            "folder is opened.");

        // And it was never a record: six files went in, six come out.
        Assert.Equal(6, snapshot.Total);
    }

    [Fact]
    public async Task Get_session_and_get_pair_return_the_same_object()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        var session = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "GET /session (SERVER_SPEC.md § 10.2)");
        var pair = (await client.GetPairAsync()).ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.3: GET /session/pair returns the identical SessionSnapshot — a convenience " +
            "alias, not a narrower resource");

        Assert.Equal(session.SessionId, pair.SessionId);
        Assert.Equal(session.PairSeq, pair.PairSeq);
        Assert.Equal(session.PairToken, pair.PairToken);
        Assert.Equal(session.PairIds, pair.PairIds);

        // Both are pure reads: neither advances the pair nor counts an impression (§ 7.4.10).
        Assert.Equal(0, session.PairSeq);
        Assert.Equal(0, pair.PairSeq);
    }

    [Fact]
    public async Task Closing_releases_the_lock_writes_nothing_and_is_idempotent_to_a_404()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.False(File.Exists(folder.File("rankmaster_db.json")),
            "Nothing has been voted on, so nothing should have been written yet.");

        var close = await client.CloseSessionAsync();
        close.ShouldHaveStatus(204, "SERVER_SPEC.md § 10.4: DELETE /session answers 204");

        Assert.False(File.Exists(folder.File(".rankmaster.lock")),
            "SERVER_SPEC.md § 10.4: closing deletes <folder>/.rankmaster.lock.");

        // § 10.4: "It MUST NOT write rankmaster_db.json. There is nothing to write."
        Assert.False(File.Exists(folder.File("rankmaster_db.json")),
            "SERVER_SPEC.md § 10.4 and § 13.2: DELETE /session writes no JSON. This mirrors Esc in SPEC.md — " +
            "the pair on screen is unseen and no extra catalog write happens.");

        var again = await client.CloseSessionAsync();
        again.ShouldBeError("no_session",
            "SERVER_SPEC.md § 10.4: a repeated DELETE is 404 no_session, which a client must not treat as a failure");
    }

    [Fact]
    public async Task The_lock_file_is_held_while_the_session_is_open_and_is_not_a_media_record()
    {
        using var folder = LibraryFolder.SixStills();
        var snapshot = await OpenAsync(folder);

        Assert.True(File.Exists(folder.File(".rankmaster.lock")),
            "SERVER_SPEC.md § 10.4: <folder>/.rankmaster.lock is created at open and held for the life of the session.");

        // § 10.4: "It is invisible to the library ... never scanned, never ranked and never appears
        // as a media id." Six files went in; six records must come out.
        Assert.Equal(6, snapshot.Total);
        Assert.DoesNotContain(".rankmaster.lock", snapshot.PairIds);
    }

    [Fact]
    public async Task Every_session_endpoint_is_a_no_session_while_none_is_open()
    {
        var client = await ClientAsync();

        foreach (var (method, path, body) in new (string, string, object?)[]
                 {
                     ("GET", "/session", null),
                     ("GET", "/session/pair", null),
                     ("DELETE", "/session", null),
                     ("POST", "/session/save", new { }),
                     ("POST", "/session/vote", new { pairToken = "x", winner = "left" }),
                     ("POST", "/session/skip", new { pairToken = "x" }),
                     ("POST", "/session/discard", new { pairToken = "x", side = "left" }),
                     ("POST", "/session/special", new { pairToken = "x", side = "left" }),
                     ("POST", "/session/undo", new { }),
                 })
        {
            var response = await client.SendAsync(new HttpMethod(method), path, body);
            var failure = response.ShouldBeError("no_session",
                $"SERVER_SPEC.md § 7.1: with no session open, {method} {path} is 404 no_session");

            // § 4: error.session is present iff a session is open — and none is.
            Assert.True(failure.Session is null,
                "SERVER_SPEC.md § 4: error.session is present iff a session is open at the moment the error " +
                "is produced. No session is open here, so it must be absent.");
        }
    }

    [Fact]
    public async Task A_videos_only_folder_ranks_videos()
    {
        using var folder = LibraryFolder.VideosOnly();
        var snapshot = await OpenAsync(folder);

        Assert.Equal("video", snapshot.Policy);
        Assert.Equal(2, snapshot.Videos);
        Assert.Equal(0, snapshot.Stills);
        Assert.Equal(2, snapshot.Rankable);
        Assert.Equal("video", snapshot.Left.Kind);
    }

    [Fact]
    public async Task A_mixed_folder_ranks_stills_and_keeps_the_video_visible()
    {
        using var folder = LibraryFolder.Mixed();
        var snapshot = await OpenAsync(folder);

        // SPEC.md § Media policy: a mixed folder is stills only. SERVER_SPEC.md § 7.4.7 and § 16.8
        // warn that the videos stay in the counts and stay fetchable while never reaching a pair.
        Assert.Equal("still", snapshot.Policy);
        Assert.Equal(4, snapshot.Total);
        Assert.Equal(3, snapshot.Stills);
        Assert.Equal(1, snapshot.Videos);
        Assert.Equal(3, snapshot.Rankable);

        Assert.All(snapshot.PairIds, id =>
            Assert.True(id != MediaFixtures.Video,
                "SPEC.md § Media policy: in a mixed folder a video can never appear in a pair."));
    }

    [Fact]
    public async Task A_two_file_library_has_no_warm_pairs_and_that_is_not_an_error()
    {
        using var folder = LibraryFolder.TwoStills();
        var snapshot = await OpenAsync(folder);

        // § 9.5 and § 16.7: with exactly two eligible files every id is reserved by Current, so
        // Pick() has nothing left to return. A client that waits for a warm pair before rendering
        // will hang forever.
        Assert.Equal(0, snapshot.WarmPairCount);
        Assert.Equal(2, snapshot.Rankable);
        Assert.Equal("ranking", snapshot.State);
        Assert.NotNull(snapshot.PairToken);
    }
}

/// <summary>
/// The parts of the session lifecycle that only exist while a rename is running, held at an exact
/// step by <see cref="RenameGateServer"/>'s blockable catalog and journal writer. A six-file rename
/// on this box is over in a millisecond, so without a gate these would be three coin tosses.
/// </summary>
[Collection(RenameGateCollection.Name)]
public sealed class SessionLifecycleDuringRenameTests(RenameGateServer server) : IAsyncLifetime
{
    private readonly RenameGateServer _server = server;

    public Task InitializeAsync() => _server.ResetAsync();
    public Task DisposeAsync() => _server.ResetAsync();

    /// <summary>
    /// AUDIT.md H14 and SERVER_SPEC.md § 10.4. Closing releases the folder lock; doing that while the
    /// rename task is still moving files would let a second <c>POST /session</c> run journal recovery
    /// on a folder that is being renamed underneath it — two programs finalizing the same plan at
    /// once. The refusal makes that race unbuildable. The phone used to do exactly this, unasked.
    /// </summary>
    [Fact]
    public async Task Close_during_a_rename_is_refused()
    {
        using var folder = LibraryFolder.SixStills();
        var client = _server.Client;
        (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        var gate = new TaskCompletionSource<bool>();
        _server.Catalog.SaveGate = gate;

        (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
        await RenamePolling.PollUntilAsync(
            client, op => op.GetProperty("phase").GetString() == "saving", timeoutSeconds: 5);

        var refused = await client.CloseSessionAsync();
        refused.ShouldBeError("rename_in_progress",
            "SERVER_SPEC.md § 10.4: DELETE /session is refused while a rename runs, like every other " +
            "mutating call. The client cancels the rename first (AUDIT.md H14).");

        Assert.NotNull(refused.JsonBody.GetProperty("error").GetProperty("details")
                              .GetProperty("operationId").GetString());

        Assert.True(File.Exists(folder.File(".rankmaster.lock")),
            "the refusal has to mean the lock is still held — that is the whole point of it.");

        gate.SetResult(true);
        var final = await RenamePolling.PollToTerminalAsync(client);
        Assert.Equal("succeeded", final.GetProperty("state").GetString());

        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "the close lands once the rename has settled");
    }

    /// <summary>
    /// SERVER_SPEC.md § 7.2 and § 10.16, "Cancel or failure — the session effect": a cancel in
    /// <c>renaming</c> keeps <c>sessionVotes</c> and <c>cues</c>. The cancel path used to call
    /// <c>Start()</c>, which zeroes exactly the two counters § 10.1 goes out of its way to protect on
    /// a resuming <c>POST /session</c> — so pressing Cancel told the owner he had cast no votes this
    /// sitting (AUDIT.md A1, C11).
    ///
    /// <para>The cancel lands while a move is retrying, which is the case the owner asked for: on his
    /// slow USB drive he wants the button to answer within one retry interval, not after the whole
    /// budget. The retry is forced by parking a <b>directory</b> at the name the first move is about
    /// to use — read out of the journal, which is fsynced before any file moves.</para>
    /// </summary>
    [Fact]
    public async Task A_cancelled_rename_keeps_the_session_counters()
    {
        using var folder = LibraryFolder.SixStills();
        var client = _server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var voted = (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote");
        Assert.Equal(1, voted.SessionVotes);
        Assert.Single(voted.Cues);

        // The gate is inside the journal write, which POST /session/rename holds the session gate
        // across — so the request is still in flight while the directory is planted, and it is
        // awaited afterwards.
        var afterWrite = new TaskCompletionSource<bool>();
        _server.JournalWriter.AfterWriteGate = afterWrite;

        var startTask = client.StartRenameAsync();
        var blocked = await BlockFirstMoveAsync(folder);
        afterWrite.SetResult(true);
        (await startTask).ShouldHaveStatus(202, "start");

        (await client.CancelRenameAsync()).ShouldHaveStatus(200, "cancel while the first move is retrying");

        var final = await RenamePolling.PollToTerminalAsync(client);
        Assert.Equal("cancelled", final.GetProperty("state").GetString());
        Directory.Delete(blocked);

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after the cancel");

        // § 7.2's row, field by field.
        Assert.Equal(opened.SessionId, after.SessionId);
        Assert.Equal(voted.SessionVotes, after.SessionVotes);
        Assert.Equal(voted.Cues, after.Cues);
        Assert.Equal(voted.PairSeq + 1, after.PairSeq);
        Assert.False(after.UndoAvailable);
        Assert.Null(after.LastAction);

        (await client.VoteAsync(voted.RequireToken("before the cancel"), "left"))
            .ShouldBeError("stale_pair_token", "pairSeq advanced, so every token the client held is stale");

        foreach (var id in after.PairIds)
            Assert.True(File.Exists(folder.File(id)), $"the session was resynced from disk; '{id}' is not there");

        Assert.False(RenameEngine.JournalExists(folder.Path), "a cancel that reunited deletes the journal");
    }

    /// <summary>
    /// The other half of the same row: a rename that <b>fails</b> after the journal was written does
    /// exactly what a cancel does to the session, because the folder is in the same condition either
    /// way. § 7.2 promised "cancelled <b>or failed</b> → resynced from disk" from the beginning and
    /// only the cancel did it (AUDIT.md H2, C1).
    /// </summary>
    [Fact]
    public async Task A_failed_rename_resyncs_like_a_cancel()
    {
        using var folder = LibraryFolder.SixStills();
        var client = _server.Client;

        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        var voted = (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote");

        var afterWrite = new TaskCompletionSource<bool>();
        _server.JournalWriter.AfterWriteGate = afterWrite;

        // Nobody cancels this one, so the move exhausts its retry budget and the run fails.
        var startTask = client.StartRenameAsync();
        var blocked = await BlockFirstMoveAsync(folder);
        afterWrite.SetResult(true);
        (await startTask).ShouldHaveStatus(202, "start");

        var final = await RenamePolling.PollToTerminalAsync(client, timeoutSeconds: 30);
        Assert.Equal("failed", final.GetProperty("state").GetString());
        Assert.Equal("rename_failed", final.GetProperty("error").GetProperty("code").GetString());
        Assert.True(final.GetProperty("error").GetProperty("reunited").GetBoolean(),
            "the reunite after the failed move could write, so no rating is in doubt");
        Directory.Delete(blocked);

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after the failure");

        Assert.Equal(opened.SessionId, after.SessionId);
        Assert.Equal(voted.SessionVotes, after.SessionVotes);
        Assert.Equal(voted.Cues, after.Cues);
        Assert.Equal(voted.PairSeq + 1, after.PairSeq);
        Assert.False(after.UndoAvailable);
        Assert.Null(after.LastAction);

        foreach (var id in after.PairIds)
            Assert.True(File.Exists(folder.File(id)),
                $"SERVER_SPEC.md § 7.2: a failed rename resyncs from disk; '{id}' is not on disk.");

        // And the point of H2: a vote after the failure actually reaches the file. § 13.1 moved the
        // moment it reaches the file — within the bound, or at once on POST /session/save — not
        // whether it does, which is what H2 was about.
        var before = await File.ReadAllTextAsync(folder.File("rankmaster_db.json"));
        (await client.VoteAsync(after.RequireToken("after the failure"), "left"))
            .ShouldBeSnapshot(200, "vote after the failed rename");
        (await client.SaveAsync()).ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.5: make the vote durable");
        Assert.NotEqual(before, await File.ReadAllTextAsync(folder.File("rankmaster_db.json")));
    }

    /// <summary>
    /// Reads the journal the rename has just fsynced and parks a <b>directory</b> at the first plan
    /// entry's destination. <c>File.Move</c> onto a directory is an I/O error, so
    /// <c>FileOps.MoveWithRetry</c> retries it — twenty times, fifty milliseconds apart — polling the
    /// cancel flag between attempts. That is a full second in which a test (or the owner) can press
    /// Cancel, and it is the exact situation § 10.16 describes: "a move whose destination or source is
    /// briefly held by another program".
    /// <para>Returns the directory it created, so the caller can remove it once the run is terminal.</para>
    /// </summary>
    private static async Task<string> BlockFirstMoveAsync(LibraryFolder folder)
    {
        var journalPath = RenameEngine.JournalPath(folder.Path);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(journalPath))
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException("The rename journal was never written.");
            await Task.Delay(5);
        }

        var journal = RenameEngine.ReadJournal(folder.Path)
            ?? throw new Xunit.Sdk.XunitException("The rename journal is present but empty.");
        var first = journal.Plan![0];

        var blocked = folder.File(first.New);
        Directory.CreateDirectory(blocked);
        return blocked;
    }
}
