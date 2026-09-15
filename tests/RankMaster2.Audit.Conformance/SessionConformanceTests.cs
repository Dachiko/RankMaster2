using System.Text.Json;
using RankMaster2.Audit.Conformance.Audit;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Conformance;

/// <summary>
/// SERVER_SPEC.md § 7 (the state machine) and § 10.1–10.5 (open, read, close, save).
/// </summary>
public sealed class SessionConformanceTests(Rm2Server server) : AuditTestBase(server)
{
    // ---- § 7.1, closed --------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/session")]
    [InlineData("GET", "/session/pair")]
    [InlineData("DELETE", "/session")]
    [InlineData("POST", "/session/save")]
    [InlineData("POST", "/session/vote")]
    [InlineData("POST", "/session/skip")]
    [InlineData("POST", "/session/discard")]
    [InlineData("POST", "/session/special")]
    [InlineData("POST", "/session/undo")]
    [InlineData("GET", "/media/alpha.jpg/meta")]
    [InlineData("GET", "/media/alpha.jpg/still")]
    [InlineData("GET", "/media/alpha.jpg/thumb")]
    [InlineData("GET", "/media/clip.avi/video")]
    public async Task WithNoSessionOpenEveryCallIsNoSession(string method, string path)
    {
        var client = await ClientAsync();
        var body = method == "POST" ? new { pairToken = "x", winner = "left", side = "left" } : null;
        var response = await client.SendAsync(new HttpMethod(method), path, body);

        response.Error("no_session",
            "SERVER_SPEC.md § 7.1: in state `closed`, \"every /session* and /media/* call → " +
            $"404 no_session\" ({method} {path})");
    }

    [Fact]
    public async Task NoSessionOutranksAMalformedMediaId()
    {
        var client = await ClientAsync();
        var response = await client.GetAsync("/media/..%2Fescape.jpg/still");

        response.Error("no_session",
            "SERVER_SPEC.md § 11.2 fixes the resolution order: \"1. No session open → 404 no_session\" " +
            "comes before \"2. Id fails § 11.1\". A malformed id with no session open is still no_session");
    }

    // ---- § 10.1, opening ------------------------------------------------------------------------

    [Fact]
    public async Task OpeningARankableFolderIs201WithALocationHeader()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var response = await client.OpenSessionAsync(folder.Path);
        var snapshot = response.RequireSnapshot(201,
            "SERVER_SPEC.md § 10.1.5: \"Returns true → … 201 with Location: /api/v1/session\"");

        response.Header("Location", "/api/v1/session",
            "SERVER_SPEC.md § 10.1.5 and openapi.yaml: the 201 carries `Location: /api/v1/session`");

        Assert.Equal(0, snapshot.PairSeq);
        Assert.Equal(0, snapshot.SessionVotes);
        Assert.Empty(snapshot.Cues);
        Assert.Null(snapshot.LastAction);
        Assert.Null(snapshot.LastSavedAt);
        Assert.False(snapshot.UndoAvailable);
    }

    [Fact]
    public async Task AFreshlyOpenedSessionHasNotSavedYet()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var snapshot = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        Assert.True(snapshot.LastSavedAt is null,
            "SERVER_SPEC.md § 10.1: \"Start() does not save. A freshly opened session has lastSavedAt: null " +
            "even though rankmaster_db.json may be older — that field describes this session's writes, not " +
            $"the file's age.\" Got '{snapshot.LastSavedAt}'.");

        Assert.False(File.Exists(Path.Combine(folder.Path, "rankmaster_db.json")),
            "SERVER_SPEC.md § 13.2: POST /session reads the JSON and writes nothing.");
    }

    [Fact]
    public async Task OpeningTheSameFolderAgainIsAPureRead()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var first = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        // Give the session state worth losing: a vote leaves a cue and a session vote count behind,
        // and § 10.1 exists precisely so a resuming phone does not lose them.
        var voted = (await client.VoteAsync(first.RequireToken("§ 8"), "left"))
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.6");

        var resumed = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(200,
                "SERVER_SPEC.md § 10.1.2: the same folder is \"200 with the live snapshot\"");

        Assert.Equal(voted.SessionId, resumed.SessionId);
        Assert.True(resumed.PairSeq == voted.PairSeq,
            "SERVER_SPEC.md § 10.1.2 and § 7.2: resuming is \"a pure read and MUST NOT change pairSeq\". " +
            $"pairSeq went {voted.PairSeq} → {resumed.PairSeq}.");
        Assert.True(resumed.SessionVotes == voted.SessionVotes,
            "SERVER_SPEC.md § 10.1.2: \"Start() MUST NOT be called again: it clears cues, SessionVotes and " +
            "the recent-shown set, which would silently discard a resuming phone's session state.\" " +
            $"sessionVotes went {voted.SessionVotes} → {resumed.SessionVotes}.");
        Assert.True(resumed.Cues.SequenceEqual(voted.Cues),
            $"SERVER_SPEC.md § 10.1.2: resuming must not clear cues. [{string.Join(",", voted.Cues)}] → " +
            $"[{string.Join(",", resumed.Cues)}].");
        Assert.True(resumed.PairToken == voted.PairToken,
            "SERVER_SPEC.md § 8.3: POST /session leaves pairSeq alone, so the token the client already " +
            "holds stays current.");
    }

    [Fact]
    public async Task ResumingAcceptsAPathThatOnlyResolvesToTheOpenFolder()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var first = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        // "Resolve with Path.GetFullPath and trim trailing separators", then compare (§ 10.1.1–2).
        var awkward = folder.Path + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;
        var resumed = await client.OpenSessionAsync(awkward);

        var snapshot = resumed.RequireSnapshot(200,
            "SERVER_SPEC.md § 10.1.1: the folder is resolved with Path.GetFullPath and trailing separators " +
            $"trimmed before the comparison, so '{awkward}' is the folder already open");
        Assert.Equal(first.SessionId, snapshot.SessionId);
    }

    [Fact]
    public async Task TheFolderInASnapshotIsFullyResolvedAndUntrailed()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var snapshot = (await client.OpenSessionAsync(folder.Path + Path.DirectorySeparatorChar))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        Assert.Equal(Path.GetFullPath(folder.Path), snapshot.Folder);
        Assert.Equal(Path.GetFileName(folder.Path), snapshot.FolderName);
    }

    [Fact]
    public async Task OpeningADifferentFolderIsAConflictAndChangesNothing()
    {
        using var first = LibraryFolder.SixStills();
        using var second = LibraryFolder.TwoStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(first.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var conflict = await client.OpenSessionAsync(second.Path);
        var error = conflict.Error("session_already_open",
            "SERVER_SPEC.md § 10.1.2: a different folder while one is open is 409 session_already_open, " +
            "and \"the server MUST NOT close the open session implicitly\"");

        var details = conflict.Details(error, "SERVER_SPEC.md § 5.3: session_already_open carries details.openFolder");
        Assert.Equal(Path.GetFullPath(first.Path), details.GetProperty("openFolder").GetString());

        var after = (await client.GetSessionAsync())
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.2");
        Assert.Equal(opened.SessionId, after.SessionId);
        Assert.Equal(Path.GetFullPath(first.Path), after.Folder);
    }

    [Fact]
    public async Task ASessionAlreadyOpenConflictCarriesTheSnapshot()
    {
        using var first = LibraryFolder.SixStills();
        using var second = LibraryFolder.TwoStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(first.Path);

        var conflict = await client.OpenSessionAsync(second.Path);
        var error = conflict.Error("session_already_open", "SERVER_SPEC.md § 10.1.2");

        Assert.True(error.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object,
            "SERVER_SPEC.md § 4: error.session \"MUST be present for every 409 on a /session/* endpoint\". " +
            $"This 409 on POST /session carries {(error.TryGetProperty("session", out var s) ? s.ValueKind.ToString() : "no session key")}.\n" +
            conflict.Describe());
    }

    // ---- § 10.1, refusals -------------------------------------------------------------------------

    [Fact]
    public async Task MissingFolderFieldIsMissingField()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{}");

        var error = response.Error("missing_field",
            "SERVER_SPEC.md § 5.2: \"a required field is absent or null\" → 400 missing_field");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2: missing_field carries details.field");
        Assert.Equal("folder", details.GetProperty("field").GetString());
    }

    [Fact]
    public async Task NullFolderIsMissingFieldNotInvalidPath()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{\"folder\": null}");

        response.Error("missing_field",
            "SERVER_SPEC.md § 5.2: missing_field covers \"a required field is absent **or null**\", so a " +
            "literal null folder is missing_field rather than invalid_path");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("./relative")]
    public async Task EmptyOrRelativeFolderIsInvalidPath(string value)
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session",
            $"{{\"folder\":{JsonSerializer.Serialize(value)}}}");

        var code = response.ErrorCodeOrThrow("SERVER_SPEC.md § 4");
        Assert.True(code == "invalid_path" || (value.Length == 0 && code == "missing_field"),
            $"SERVER_SPEC.md § 10.1.1: \"Validate folder: non-empty, rooted, no NUL → else 400 invalid_path\". " +
            $"'{value}' answered '{code}' at HTTP {response.StatusCode}.\n{response.Describe()}");
        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task FolderContainingNulIsInvalidPath()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{\"folder\": \"/tmp/a\\u0000b\"}");

        response.Error("invalid_path",
            "SERVER_SPEC.md § 10.1.1: a folder containing a NUL is 400 invalid_path");
    }

    [Fact]
    public async Task FolderOverFourThousandNinetySixCharactersIsInvalidPath()
    {
        var client = await ClientAsync();
        var path = "/" + new string('a', 5000);
        var response = await client.SendAsync(HttpMethod.Post, "/session",
            $"{{\"folder\":{JsonSerializer.Serialize(path)}}}");

        response.Error("invalid_path",
            "SERVER_SPEC.md § 15: \"folder / path | 4096 characters | 400 invalid_path\"");
    }

    [Fact]
    public async Task AMissingFolderIsFolderNotFound()
    {
        var client = await ClientAsync();
        var missing = Path.Combine(Path.GetTempPath(), $"rm2-audit-absent-{Guid.NewGuid():N}");
        var response = await client.OpenSessionAsync(missing);

        var error = response.Error("folder_not_found",
            "SERVER_SPEC.md § 10.1.3: \"Folder missing → 404 folder_not_found\"");
        var details = response.Details(error, "SERVER_SPEC.md § 5.3: folder_not_found carries details.folder");
        Assert.Contains(Path.GetFileName(missing), details.GetProperty("folder").GetString()!);
    }

    [Fact]
    public async Task APathThatIsAFileIsFolderNotADirectory()
    {
        var client = await ClientAsync();
        var file = Path.Combine(Path.GetTempPath(), $"rm2-audit-file-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "not a directory");
        try
        {
            var response = await client.OpenSessionAsync(file);
            var error = response.Error("folder_not_a_directory",
                "SERVER_SPEC.md § 10.1.3: \"a file → 400 folder_not_a_directory\"");
            response.Details(error, "SERVER_SPEC.md § 5.3: folder_not_a_directory carries details.folder");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AFolderWithOneStillIsNotRankable()
    {
        using var folder = LibraryFolder.OneStill();
        var client = await ClientAsync();

        var response = await client.OpenSessionAsync(folder.Path);
        var error = response.Error("folder_not_rankable",
            "SERVER_SPEC.md § 10.1.5: \"Returns false → release the lock, discard the session → " +
            "409 folder_not_rankable with details counts\"");

        var details = response.Details(error,
            "SERVER_SPEC.md § 5.3: folder_not_rankable carries {stills, videos, rankable}");
        Assert.Equal(1, details.GetProperty("stills").GetInt32());
        Assert.Equal(0, details.GetProperty("videos").GetInt32());
        Assert.Equal(1, details.GetProperty("rankable").GetInt32());
    }

    [Fact]
    public async Task AnEmptyFolderIsNotRankableWithZeroCounts()
    {
        using var folder = LibraryFolder.Empty();
        var client = await ClientAsync();

        var response = await client.OpenSessionAsync(folder.Path);
        var error = response.Error("folder_not_rankable", "SERVER_SPEC.md § 10.1.5");
        var details = response.Details(error, "SERVER_SPEC.md § 5.3");

        Assert.Equal(0, details.GetProperty("stills").GetInt32());
        Assert.Equal(0, details.GetProperty("videos").GetInt32());
        Assert.Equal(0, details.GetProperty("rankable").GetInt32());
    }

    [Fact]
    public async Task AMixedFolderWithOneStillIsNotRankableEvenWithManyVideos()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm2-audit", $"one-still-two-videos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "only.jpg"), MediaFixtures.Jpeg(200, 150));
            await File.WriteAllBytesAsync(Path.Combine(root, "a.mp4"), MediaFixtures.SmallVideo());
            await File.WriteAllBytesAsync(Path.Combine(root, "b.mp4"), MediaFixtures.SmallVideo());

            var client = await ClientAsync();
            var response = await client.OpenSessionAsync(root);

            var error = response.Error("folder_not_rankable",
                "SPEC.md § Media policy: a mixed folder ranks stills only, so one still and two videos " +
                "leaves one eligible file and the folder must not open (SERVER_SPEC.md § 10.1.5)");
            var details = response.Details(error, "SERVER_SPEC.md § 5.3");
            Assert.Equal(1, details.GetProperty("stills").GetInt32());
            Assert.Equal(2, details.GetProperty("videos").GetInt32());
            Assert.True(details.GetProperty("rankable").GetInt32() == 1,
                "SERVER_SPEC.md § 5.3: `rankable` counts the eligible files, which under the mixed-folder " +
                $"rule is the stills alone. Got {details.GetProperty("rankable").GetInt32()}.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnreadableLibraryJsonIsRefusedAndNeverOverwritten()
    {
        using var folder = LibraryFolder.WithUnreadableDatabase();
        var databasePath = Path.Combine(folder.Path, "rankmaster_db.json");
        var before = await File.ReadAllBytesAsync(databasePath);

        var client = await ClientAsync();
        var response = await client.OpenSessionAsync(folder.Path);

        var error = response.Error("library_json_unreadable",
            "SERVER_SPEC.md § 10.1.5: \"Throws InvalidDataException from JsonCatalog.LoadRequired → " +
            "release the lock → 409 library_json_unreadable\"");
        var details = response.Details(error, "SERVER_SPEC.md § 5.3: library_json_unreadable carries details.file");
        Assert.Contains("rankmaster_db.json", details.GetProperty("file").GetString()!);

        Assert.True(before.SequenceEqual(await File.ReadAllBytesAsync(databasePath)),
            "SERVER_SPEC.md § 10.1.5: \"The server MUST NOT write the file on this path, at any later point " +
            "in the request, or during cleanup\" (SPEC.md § Persistence: refuse to start, never overwrite).");
    }

    [Fact]
    public async Task AFailedOpenReleasesTheFolderLock()
    {
        using var folder = LibraryFolder.OneStill();
        var client = await ClientAsync();

        (await client.OpenSessionAsync(folder.Path))
            .Error("folder_not_rankable", "SERVER_SPEC.md § 10.1.5");

        // § 10.1.5: "release the lock, discard the session". A lock still held would be observable
        // as a file the test cannot delete, and would wedge every later open of the same folder.
        var lockPath = Path.Combine(folder.Path, ".rankmaster.lock");
        if (File.Exists(lockPath))
        {
            var ex = Record.Exception(() =>
            {
                using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            Assert.True(ex is null,
                "SERVER_SPEC.md § 10.1 (transitions table): \"closed | POST /session unrankable | closed | " +
                $"lock MUST be released before responding\". The lock file is still held: {ex?.Message}");
        }

        // And the folder must be openable again once it becomes rankable.
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "second.jpg"), MediaFixtures.Jpeg(200, 150));
        (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "The lock from the refused open must not wedge the next attempt");
    }

    [Fact]
    public async Task AnUnreadableLibraryJsonAlsoReleasesTheLock()
    {
        using var folder = LibraryFolder.WithUnreadableDatabase();
        var client = await ClientAsync();

        (await client.OpenSessionAsync(folder.Path))
            .Error("library_json_unreadable", "SERVER_SPEC.md § 10.1.5");

        File.Delete(Path.Combine(folder.Path, "rankmaster_db.json"));
        (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201,
                "SERVER_SPEC.md § 10.1.5: the lock is released before library_json_unreadable is returned, " +
                "so a repaired folder opens straight away");
    }

    // ---- § 10.4, closing --------------------------------------------------------------------------

    [Fact]
    public async Task ClosingIs204AndWritesNoJson()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var databasePath = Path.Combine(folder.Path, "rankmaster_db.json");
        Assert.False(File.Exists(databasePath));

        var close = await client.CloseSessionAsync();
        close.Status(204, "SERVER_SPEC.md § 10.4: \"204 on success\"");
        Assert.Empty(close.Body);

        Assert.False(File.Exists(databasePath),
            "SERVER_SPEC.md § 10.4: DELETE /session \"MUST NOT write rankmaster_db.json\". This mirrors " +
            "Esc in SPEC.md — the pair on screen is unseen and no extra catalog write happens.");
    }

    [Fact]
    public async Task ClosingRemovesTheLockFile()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);
        await client.CloseSessionAsync();

        Assert.False(File.Exists(Path.Combine(folder.Path, ".rankmaster.lock")),
            "SERVER_SPEC.md § 10.4: closing \"closes and deletes <folder>/.rankmaster.lock\".");
    }

    [Fact]
    public async Task ASecondCloseIsNoSession()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);
        await client.CloseSessionAsync();

        (await client.CloseSessionAsync())
            .Error("no_session",
                "SERVER_SPEC.md § 10.4: \"404 no_session if none is open — a repeated DELETE is therefore " +
                "a 404 and MUST NOT be treated by the client as a failure\"");
    }

    [Fact]
    public async Task ClosingDoesNotCountAnImpressionForThePairOnScreen()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var ids = opened.PairIds;

        // Force a save so the JSON exists to inspect, then close on a fresh (unvoted) pair.
        var saved = (await client.SaveAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.5");
        var pairAtClose = saved.PairIds;
        await client.CloseSessionAsync();

        var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(folder.Path, "rankmaster_db.json")));
        var images = json.RootElement.GetProperty("images");

        foreach (var id in pairAtClose)
        {
            var impressions = images.GetProperty(id).GetProperty("impressions").GetInt32();
            Assert.True(impressions == 0,
                "SERVER_SPEC.md § 7.4.10: \"impressions increments only on vote and skip … The pair open " +
                $"when DELETE /session arrives is **unseen**.\" '{id}' has impressions = {impressions}.");
        }
    }

    // ---- § 10.2 / § 10.3, the two reads ------------------------------------------------------------

    [Fact]
    public async Task GetSessionAndGetPairReturnTheIdenticalSnapshot()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var session = await client.GetSessionAsync();
        var pair = await client.GetPairAsync();

        session.RequireSnapshot(200, "SERVER_SPEC.md § 10.2");
        pair.RequireSnapshot(200, "SERVER_SPEC.md § 10.3");

        Assert.True(session.Text == pair.Text,
            "SERVER_SPEC.md § 10.3: GET /session/pair \"Returns the identical SessionSnapshot … it is a " +
            "convenience alias, not a narrower resource\".\n" +
            $"GET /session      : {session.Text}\nGET /session/pair : {pair.Text}");
    }

    [Fact]
    public async Task ReadsNeverAdvanceThePairOrCountAnImpression()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        for (var i = 0; i < 3; i++)
        {
            await client.GetSessionAsync();
            await client.GetPairAsync();
        }

        var after = (await client.GetSessionAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.2");
        Assert.Equal(opened.PairSeq, after.PairSeq);
        Assert.Equal(opened.PairToken, after.PairToken);
        Assert.True(after.PairIds.SequenceEqual(opened.PairIds),
            "SERVER_SPEC.md § 10.3: \"Pure read; never changes pairSeq; never counts an impression.\"");
    }

    // ---- § 10.5, save --------------------------------------------------------------------------

    [Fact]
    public async Task SaveWritesTheFileAndSetsLastSavedAtWithoutTouchingThePair()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var saved = (await client.SaveAsync())
            .RequireSnapshot(200, "SERVER_SPEC.md § 10.5: an explicit save answers with the snapshot");

        Assert.True(saved.LastSavedAt is not null,
            "SERVER_SPEC.md § 10.5: an explicit save \"Updates lastSavedAt\".");
        Assert.Equal(opened.PairSeq, saved.PairSeq);
        Assert.Equal(opened.PairToken, saved.PairToken);
        Assert.True(File.Exists(Path.Combine(folder.Path, "rankmaster_db.json")),
            "SERVER_SPEC.md § 13.2: POST /session/save writes the JSON before the response.");
    }

    [Fact]
    public async Task SaveIsIdempotentAndSafeToRepeat()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var first = (await client.SaveAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.5");
        var second = (await client.SaveAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.5");
        var third = (await client.SaveAsync()).RequireSnapshot(200, "SERVER_SPEC.md § 10.5");

        Assert.True(first.PairSeq == second.PairSeq && second.PairSeq == third.PairSeq,
            "SERVER_SPEC.md § 10.5: \"Idempotent and always safe to retry. Never changes the pair, the " +
            "token or pairSeq.\"");
        Assert.Equal(opened.PairIds, third.PairIds);
    }

    [Fact]
    public async Task SavedJsonIsTheVersionOneSchema()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);
        await client.SaveAsync();

        using var json = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(folder.Path, "rankmaster_db.json")));
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(root.TryGetProperty("lastUpdated", out _),
            "SPEC.md § Persistence: the v1 schema carries `lastUpdated`.");

        var images = root.GetProperty("images");
        foreach (var image in images.EnumerateObject())
        {
            Assert.True(image.Value.GetProperty("filename").GetString() == image.Name,
                "SPEC.md § Persistence: \"Identity is the filename (the object key and the filename field " +
                $"must match)\". Key '{image.Name}' vs filename " +
                $"'{image.Value.GetProperty("filename").GetString()}'.");
            foreach (var field in new[] { "rating", "matches", "impressions", "lastPlayed" })
                Assert.True(image.Value.TryGetProperty(field, out _),
                    $"SPEC.md § Persistence: the v1 row carries `{field}`.");
        }
    }
}
