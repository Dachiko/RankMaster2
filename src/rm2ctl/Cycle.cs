using System.Text.Json;
using System.Text.RegularExpressions;

namespace RankMaster2.Cli;

/// <summary>
/// The whole ranking cycle, driven from the command line: open, read the pair, fetch the stills,
/// vote, skip, discard, special, undo, save, close — followed by the refusals the contract
/// specifies. Every check is recorded; the process exits non-zero if any of them failed.
///
/// This is what makes the server testable without a phone. It is written against SERVER_SPEC.md
/// rather than against the server, so a disagreement between the two shows up here as a named
/// failure rather than as a phone that mysteriously will not rank.
/// </summary>
public sealed class Cycle(Rm2Api api, Journal journal)
{
    /// <summary>
    /// Every name one rename run produces (SERVER_SPEC.md § 10.16, SPEC.md § Rename by rank):
    /// <c>NNNNNN-ssss.ext</c> — six decimal digits of rank so a byte-wise sort of the folder is rank
    /// order, then the run's four lowercase hex characters, then the file's own extension. The suffix
    /// is what makes the old and new names of a run disjoint sets, which is what lets an interrupted
    /// rename be recovered without guessing which files have already moved.
    /// </summary>
    private const string RenamedName = @"^\d{6}-[0-9a-f]{4}\.[^.]+$";

    public async Task RunAsync(string folder)
    {
        journal.Title($"Ranking cycle against {api.BaseUrl}");

        var snapshot = await OpenAsync(folder);
        if (snapshot is null) return;

        snapshot = await ReadPairAsync() ?? snapshot;
        await FetchStillsAsync(snapshot);

        snapshot = await VoteAsync(snapshot) ?? snapshot;
        snapshot = await CancelVoteAsync(snapshot) ?? snapshot;
        snapshot = await SkipAsync(snapshot) ?? snapshot;
        snapshot = await DiscardAsync(snapshot) ?? snapshot;
        snapshot = await SpecialAsync(snapshot) ?? snapshot;
        snapshot = await UndoAsync(snapshot) ?? snapshot;
        await SaveAsync(snapshot);

        await UnhappyPathsAsync(snapshot);

        // Driven last, since a successful rename renumbers everything: the unhappy-path checks
        // above depend on the ids in `snapshot` still naming real files, which a rename would break.
        await RenameAsync(snapshot);

        await CloseAsync();
    }

    // ---- rename by rank (SERVER_SPEC.md § 10.16) -------------------------------------------------

    /// <summary>
    /// The server's acceptance gate for part F: start a rename, poll it to completion, and verify
    /// the renumber carried every rating, created no backup folder, and left a loadable database.
    /// Cancel is not exercised here — it needs a folder large enough to still be moving when cancel
    /// lands (SERVER_SPEC.md § 10.16 "the honest slow-phase truth"), which this scratch folder isn't;
    /// cancel is proven deterministically instead, in RenameEngineTests and RenameTests.
    /// </summary>
    private async Task RenameAsync(Snapshot before)
    {
        journal.Title("Rename by rank");
        journal.Step($"start POST /session/rename against {before.Folder}");

        var start = await api.StartRenameAsync("rm2ctl-rename-1");
        if (!journal.Check(start.Status == 202,
                "POST /session/rename starts with 202 Accepted (SERVER_SPEC.md § 10.16)", Explain(start)))
            return;

        journal.Check(start.Json?.GetProperty("state").GetString() == "running",
            "the 202 body reports state 'running' (SERVER_SPEC.md § 10.16)");
        journal.Check(
            start.Header("Location")?.EndsWith("/api/v1/session/rename", StringComparison.Ordinal) == true,
            "a 202 carries Location: /api/v1/session/rename (SERVER_SPEC.md § 10.16)",
            $"Location was '{start.Header("Location") ?? "(absent)"}'");

        // Poll to a terminal state, asserting the phase only ever advances (SERVER_SPEC.md § 10.16:
        // preparing -> renaming -> saving -> [reuniting] -> done).
        var phaseOrder = new[] { "preparing", "renaming", "saving", "reuniting", "done" };
        var highestPhaseSeen = -1;
        var phaseWentBackwards = false;
        Reply? terminal = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var poll = await api.GetRenameAsync();
            if (!journal.Check(poll.Status == 200,
                    "GET /session/rename while polling (SERVER_SPEC.md § 10.16)", Explain(poll)))
                return;

            var phase = poll.Json?.GetProperty("phase").GetString() ?? "";
            var index = Array.IndexOf(phaseOrder, phase);
            if (index >= 0)
            {
                if (index < highestPhaseSeen) phaseWentBackwards = true;
                highestPhaseSeen = Math.Max(highestPhaseSeen, index);
            }

            var state = poll.Json?.GetProperty("state").GetString();
            if (state is "succeeded" or "cancelled" or "failed")
            {
                terminal = poll;
                break;
            }

            await Task.Delay(50);
        }

        journal.Check(!phaseWentBackwards, "the observed phase only ever advances (SERVER_SPEC.md § 10.16)");

        if (terminal is null)
        {
            journal.Failure("the rename operation reaches a terminal state within 30s (SERVER_SPEC.md § 10.16)");
            return;
        }

        journal.Detail(terminal.Summarise());
        if (!journal.Check(terminal.Json?.GetProperty("state").GetString() == "succeeded",
                "the rename reaches 'succeeded' (SERVER_SPEC.md § 10.16)", Explain(terminal)))
            return;

        var after = Snapshot.From(await api.GetSessionAsync());
        if (after is null)
        {
            journal.Failure("GET /session after a succeeded rename returns a SessionSnapshot");
            return;
        }

        journal.Detail(after.Describe());

        journal.Check(after.PairSeq == before.PairSeq + 1,
            "a succeeded rename advances pairSeq by exactly 1 (SERVER_SPEC.md § 8.3)",
            $"{before.PairSeq} -> {after.PairSeq}");
        journal.Check(after.LastActionType == "rename",
            "lastAction.type is 'rename' (SERVER_SPEC.md § 9.4)");
        journal.Check(!after.UndoAvailable, "a rename clears undo (SERVER_SPEC.md § 10.16)");

        foreach (var id in new[] { after.Left?.Id, after.Right?.Id })
        {
            if (id is null) continue;
            journal.Check(Regex.IsMatch(id, RenamedName),
                $"{id}: a renamed file's new id is NNNNNN-ssss.ext — six digits of rank, the run's " +
                "four-hex-character suffix, the file's own extension (SPEC.md § Rename by rank)");
        }

        var hasBackupFolder = Directory.Exists(before.Folder) &&
            Directory.EnumerateDirectories(before.Folder, "rankmaster_backup_*").Any();
        journal.Check(!hasBackupFolder,
            "no rankmaster_backup_* folder was created (SERVER_SPEC.md § 10.16 — the server copies no files)");

        var dbPath = Path.Combine(before.Folder, "rankmaster_db.json");
        if (!journal.Check(File.Exists(dbPath), "rankmaster_db.json exists after a succeeded rename"))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(dbPath));
            var images = document.RootElement.TryGetProperty("images", out var img) ? img : default;
            var everyKeyIsNew = images.ValueKind == JsonValueKind.Object &&
                images.EnumerateObject().All(p => Regex.IsMatch(p.Name, RenamedName));

            journal.Check(everyKeyIsNew,
                "the on-disk database still loads and every key is a name of this run — NNNNNN-ssss.ext — " +
                "the acceptance gate: no rating lost to the rename (SERVER_SPEC.md § 10.16)");

            var suffixes = images.ValueKind == JsonValueKind.Object
                ? images.EnumerateObject()
                    .Select(p => Path.GetFileNameWithoutExtension(p.Name).Split('-')[^1])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            journal.Check(everyKeyIsNew && suffixes.Count == 1,
                "one suffix for the whole run, so sorting the folder by name is sorting it by rank " +
                "(SERVER_SPEC.md § 10.16)",
                suffixes.Count == 0 ? "no names to read a suffix from" : string.Join(", ", suffixes));
        }
        catch (JsonException)
        {
            journal.Failure("rankmaster_db.json still parses as JSON after the rename");
        }
    }

    // ---- the happy path -------------------------------------------------------------------------

    private async Task<Snapshot?> OpenAsync(string folder)
    {
        journal.Step($"open a session on {folder}");

        var reply = await api.OpenSessionAsync(folder);

        if (reply.Status == 409 && reply.ErrorCode == "session_already_open")
        {
            journal.Note("another folder is already open; closing it first (the server never closes one implicitly)");
            await api.CloseSessionAsync();
            reply = await api.OpenSessionAsync(folder);
        }

        if (!journal.Check(reply.Status is 200 or 201,
                "POST /session opens a rankable folder (SERVER_SPEC.md § 10.1)",
                Explain(reply)))
            return null;

        var snapshot = Snapshot.From(reply);
        if (snapshot is null)
        {
            journal.Failure("the 2xx body is a SessionSnapshot (SERVER_SPEC.md § 9)", Explain(reply));
            return null;
        }

        journal.Detail(snapshot.Describe());

        if (reply.Status == 201)
        {
            journal.Check(reply.Header("Location")?.EndsWith("/api/v1/session", StringComparison.Ordinal) == true,
                "a 201 carries Location: /api/v1/session (SERVER_SPEC.md § 10.1)",
                $"Location was '{reply.Header("Location") ?? "(absent)"}'");

            journal.Check(snapshot.PairSeq == 0,
                "a new session starts at pairSeq 0 (SERVER_SPEC.md § 8.2)",
                $"pairSeq was {snapshot.PairSeq}");

            journal.Check(snapshot.LastSavedAt is null,
                "a freshly opened session has lastSavedAt null — Start() reads and does not write (§ 10.1)",
                $"lastSavedAt was '{snapshot.LastSavedAt}'");
        }

        journal.Check(snapshot.State == "ranking" && snapshot.PairToken is not null,
            "the folder opened into 'ranking' with a pair (SERVER_SPEC.md § 7.1)",
            $"state '{snapshot.State}', pairToken {(snapshot.PairToken is null ? "null" : "set")}");

        // § 10.1: naming the folder already open is a pure read — Start() must not run again.
        journal.Step("re-open the same folder (must resume, not restart)");
        var resumed = await api.OpenSessionAsync(folder);
        var resumedSnapshot = Snapshot.From(resumed);

        journal.Check(resumed.Status == 200,
            "re-opening the folder already open is 200, not 201 (SERVER_SPEC.md § 10.1)",
            Explain(resumed));

        if (resumedSnapshot is not null)
        {
            journal.Check(resumedSnapshot.SessionId == snapshot.SessionId &&
                          resumedSnapshot.PairSeq == snapshot.PairSeq &&
                          resumedSnapshot.PairToken == snapshot.PairToken,
                "resuming changes nothing — Start() must not run again, or it clears the cues, the vote " +
                "count and the recent-shown set of a phone that was merely resuming (§ 10.1)",
                $"sessionId {snapshot.SessionId} -> {resumedSnapshot.SessionId}, " +
                $"pairSeq {snapshot.PairSeq} -> {resumedSnapshot.PairSeq}");

            return resumedSnapshot;
        }

        return snapshot;
    }

    private async Task<Snapshot?> ReadPairAsync()
    {
        journal.Step("read the pair");

        var reply = await api.GetPairAsync();
        if (!journal.Check(reply.Status == 200, "GET /session/pair (SERVER_SPEC.md § 10.3)", Explain(reply)))
            return null;

        var snapshot = Snapshot.From(reply);
        if (snapshot is null)
        {
            journal.Failure("GET /session/pair returns the identical SessionSnapshot (§ 10.3)", Explain(reply));
            return null;
        }

        journal.Detail(snapshot.Describe());

        var session = await api.GetSessionAsync();
        var full = Snapshot.From(session);
        journal.Check(full is not null && full.PairToken == snapshot.PairToken && full.PairSeq == snapshot.PairSeq,
            "GET /session and GET /session/pair return the same object — an alias, not a narrower resource (§ 10.3)");

        journal.Check(snapshot.WarmPairCount <= snapshot.PrefetchPairs,
            $"warmPairs never exceeds prefetchPairs (SERVER_SPEC.md § 9.1)",
            $"{snapshot.WarmPairCount} warm pairs against a prefetch depth of {snapshot.PrefetchPairs}");

        if (snapshot.WarmPairCount == 0 && snapshot.Rankable == 2)
            journal.Note("no warm pairs, which is correct on a two-file library: both ids are reserved by the " +
                         "current pair, so Pick() has nothing left to return (§ 9.5)");

        return snapshot;
    }

    private async Task FetchStillsAsync(Snapshot snapshot)
    {
        journal.Step("fetch the pair's bytes");

        // What the counters read before a single byte is fetched. § 7.4.10 says fetching bytes is
        // never an impression, and that is a statement about change, not about zero.
        var beforeFetch = new Dictionary<string, int>();
        foreach (var reference in new[] { snapshot.Left, snapshot.Right })
        {
            if (reference is null) continue;
            var seen = await api.MetaAsync(reference.Id);
            if (seen.Status == 200 && seen.Json is { } seenBody)
                beforeFetch[reference.Id] = seenBody.GetProperty("impressions").GetInt32();
        }

        foreach (var reference in new[] { snapshot.Left, snapshot.Right })
        {
            if (reference is null) continue;

            if (reference.Kind == "still")
            {
                if (reference.StillLink is null)
                {
                    journal.Failure($"{reference.Id}: a still has a links.still (SERVER_SPEC.md § 9.3)");
                    continue;
                }

                // § 9.3: clients should use the links verbatim, which removes every id-encoding bug
                // from four client implementations at once.
                var still = await api.FollowAsync(reference.StillLink);
                journal.Check(still.Status == 200,
                    $"{reference.Id}: links.still is usable verbatim (SERVER_SPEC.md § 9.3)", Explain(still));

                if (still.Status == 200)
                {
                    journal.Detail($"{still.Body.Length} bytes, {still.ContentType}, ETag {still.Header("ETag")}");

                    var cacheControl = still.Header("Cache-Control") ?? "";
                    journal.Check(cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase),
                        $"{reference.Id}: media bytes are `private` — these are the user's photos and no shared " +
                        "cache may hold them (SERVER_SPEC.md § 12.5)",
                        $"Cache-Control was '{cacheControl}'");

                    var tag = still.Header("ETag");
                    journal.Check(tag is not null && !tag.StartsWith("W/", StringComparison.Ordinal),
                        $"{reference.Id}: the ETag is strong (SERVER_SPEC.md § 12.2)",
                        $"ETag was '{tag ?? "(absent)"}'");

                    if (tag is not null)
                    {
                        var revalidated = await api.StillAsync(reference.Id, ifNoneMatch: tag);
                        journal.Check(revalidated.Status == 304,
                            $"{reference.Id}: an exact If-None-Match is 304 (SERVER_SPEC.md § 12.2)",
                            Explain(revalidated));
                    }
                }

                var thumb = await api.ThumbAsync(reference.Id);
                journal.Check(thumb.Status == 200,
                    $"{reference.Id}: the 320 px thumbnail (SERVER_SPEC.md § 12.1)", Explain(thumb));

                journal.Check(reference.VideoLink is null,
                    $"{reference.Id}: links.video is null for a still (SERVER_SPEC.md § 9.3)");
            }
            else
            {
                journal.Check(reference.StillLink is null && reference.ThumbLink is null,
                    $"{reference.Id}: a video has no still and no thumb — there are no poster frames (§ 9.3)");

                var video = await api.VideoAsync(reference.Id, range: "bytes=0-99");
                journal.Check(video.Status == 206,
                    $"{reference.Id}: a satisfiable Range is 206 (SERVER_SPEC.md § 12.4)", Explain(video));
            }

            var meta = await api.MetaAsync(reference.Id);
            journal.Check(meta.Status == 200,
                $"{reference.Id}: GET /media/{{id}}/meta (SERVER_SPEC.md § 12.1)", Explain(meta));


            if (meta.Status == 200 && meta.Json is { } body)
            {
                journal.Check(body.GetProperty("id").GetString() == reference.Id,
                    $"{reference.Id}: meta echoes the on-disk spelling (SERVER_SPEC.md § 11.1 step 6)");

                // Compared against the reading taken before any bytes were fetched, not against
                // zero: a folder that has been ranked before starts above zero and the rule being
                // checked is that fetching does not move it.
                var now = body.GetProperty("impressions").GetInt32();
                journal.Check(!beforeFetch.TryGetValue(reference.Id, out var was) || now == was,
                    $"{reference.Id}: fetching bytes is not an impression (SERVER_SPEC.md § 7.4.10)",
                    $"impressions {(beforeFetch.TryGetValue(reference.Id, out var w) ? w : 0)} -> {now}");
            }
        }
    }

    private async Task<Snapshot?> VoteAsync(Snapshot before)
    {
        journal.Step("vote");

        if (before.PairToken is null)
        {
            journal.Failure("there is a pair to vote on");
            return null;
        }

        var winner = before.Left!.Id;
        var loser = before.Right!.Id;

        // Read first, compare after. Asserting matches == 1 only holds on a folder nobody has ever
        // ranked, and the folder someone reaches for is usually their own.
        var counted = new Dictionary<string, (int Matches, int Impressions)>();
        foreach (var id in new[] { winner, loser })
        {
            var meta = await api.MetaAsync(id);
            if (meta.Status == 200 && meta.Json is { } body)
            {
                counted[id] = (body.GetProperty("matches").GetInt32(),
                               body.GetProperty("impressions").GetInt32());
            }
        }

        var reply = await api.VoteAsync(before.PairToken, "left", "rm2ctl-vote-1");
        if (!journal.Check(reply.Status == 200, "POST /session/vote (SERVER_SPEC.md § 10.6)", Explain(reply)))
            return null;

        var after = Snapshot.From(reply);
        if (after is null) return null;

        journal.Detail(after.Describe());
        journal.Note($"voted for {winner} over {loser}");

        journal.Check(after.PairSeq == before.PairSeq + 1,
            "a successful vote advances pairSeq by exactly 1 (SERVER_SPEC.md § 8.3)",
            $"{before.PairSeq} -> {after.PairSeq}");

        journal.Check(after.SessionVotes == before.SessionVotes + 1,
            "a vote increments sessionVotes (SERVER_SPEC.md § 9.1)",
            $"{before.SessionVotes} -> {after.SessionVotes}");

        journal.Check(after.Cues.Length == before.Cues.Length + 1,
            "a vote appends exactly one cue (SERVER_SPEC.md § 9.1)",
            $"{before.Cues.Length} -> {after.Cues.Length} cues");

        journal.Check(after.PairToken != before.PairToken,
            "the pairToken changes on every advance, even when the ids repeat (SERVER_SPEC.md § 7.4.1)");

        journal.Check(after.LastActionType == "vote" &&
                      after.LastActionField("clientRequestId") == "rm2ctl-vote-1" &&
                      after.LastActionField("pairToken") == before.PairToken,
            "lastAction records the vote, the token it consumed and the clientRequestId (SERVER_SPEC.md § 9.4)",
            $"lastAction was {after.LastAction?.ToString() ?? "null"}");

        // SERVER_SPEC.md § 13.1: a vote's 200 means applied, and on disk within the bound — at most
        // SaveDelaySeconds or MaxUnsavedChoices further choices. POST /session/save is what turns
        // that into "on disk now" (§ 10.5), and it is what every on-disk check here asks for first.
        var durable = await api.SaveAsync();
        if (journal.Check(durable.Status == 200,
                "POST /session/save after a vote makes the point durable (SERVER_SPEC.md § 10.5)",
                Explain(durable)))
        {
            journal.Check(Snapshot.From(durable)?.LastSavedAt is not null,
                "the 200 from POST /session/save means the vote is fsynced and in place (SERVER_SPEC.md § 13.1)",
                "lastSavedAt is still null after a successful save");
        }

        // § 10.6: both records get matches + 1 and impressions + 1.
        foreach (var id in new[] { winner, loser })
        {
            var meta = await api.MetaAsync(id);
            if (meta.Status != 200 || meta.Json is not { } body) continue;
            if (!counted.TryGetValue(id, out var was)) continue;

            var matches = body.GetProperty("matches").GetInt32();
            var impressions = body.GetProperty("impressions").GetInt32();

            journal.Check(matches == was.Matches + 1 && impressions == was.Impressions + 1,
                $"{id}: a vote sets matches + 1 and impressions + 1 on both records (SERVER_SPEC.md § 10.6)",
                $"matches {was.Matches} -> {matches}, impressions {was.Impressions} -> {impressions}");
        }

        return after;
    }

    private async Task<Snapshot?> SkipAsync(Snapshot before)
    {
        journal.Step("skip");

        if (before.PairToken is null) return null;

        var ids = new[] { before.Left!.Id, before.Right!.Id };
        var priors = new List<(string Id, int Matches, int Impressions)>();
        foreach (var id in ids)
        {
            var meta = await api.MetaAsync(id);
            if (meta.Json is { } body)
                priors.Add((id, body.GetProperty("matches").GetInt32(), body.GetProperty("impressions").GetInt32()));
        }

        var reply = await api.SkipAsync(before.PairToken, "rm2ctl-skip-1");
        if (!journal.Check(reply.Status == 200, "POST /session/skip (SERVER_SPEC.md § 10.7)", Explain(reply)))
            return null;

        var after = Snapshot.From(reply);
        if (after is null) return null;

        journal.Detail(after.Describe());

        journal.Check(after.PairSeq == before.PairSeq + 1,
            "a skip advances pairSeq by 1 (SERVER_SPEC.md § 8.3)", $"{before.PairSeq} -> {after.PairSeq}");

        journal.Check(after.SessionVotes == before.SessionVotes,
            "a skip does not count as a vote (SERVER_SPEC.md § 9.1)",
            $"sessionVotes {before.SessionVotes} -> {after.SessionVotes}");

        journal.Check(after.Cues.Length == before.Cues.Length,
            "a skip appends no cue (SERVER_SPEC.md § 9.1)",
            $"{before.Cues.Length} -> {after.Cues.Length} cues");

        foreach (var (id, matches, impressions) in priors)
        {
            var meta = await api.MetaAsync(id);
            if (meta.Json is not { } body) continue;

            journal.Check(body.GetProperty("impressions").GetInt32() == impressions + 1,
                $"{id}: a skip is impressions + 1 (SERVER_SPEC.md § 10.7)",
                $"{impressions} -> {body.GetProperty("impressions").GetInt32()}");

            journal.Check(body.GetProperty("matches").GetInt32() == matches,
                $"{id}: a skip makes no matches change (SERVER_SPEC.md § 10.7)",
                $"{matches} -> {body.GetProperty("matches").GetInt32()}");
        }

        return after;
    }

    private async Task<Snapshot?> DiscardAsync(Snapshot before)
    {
        journal.Step("discard");

        if (before.PairToken is null) return null;
        var discarded = before.Left!.Id;

        var reply = await api.DiscardAsync(before.PairToken, "left", "rm2ctl-discard-1");
        if (!journal.Check(reply.Status == 200, "POST /session/discard (SERVER_SPEC.md § 10.8)", Explain(reply)))
            return null;

        var after = Snapshot.From(reply);
        if (after is null) return null;

        journal.Detail(after.Describe());
        journal.Note($"discarded {discarded}");

        journal.Check(after.Total == before.Total - 1,
            "a discard removes the record (SERVER_SPEC.md § 10.8)",
            $"total {before.Total} -> {after.Total}");

        journal.Check(after.LastActionType == "discard" && after.LastActionField("id") == discarded,
            "lastAction records the discard and the id it moved (SERVER_SPEC.md § 9.4)");

        journal.Check(after.UndoAvailable,
            "a recorded move in this folder makes undoAvailable true (SERVER_SPEC.md § 9.1)");

        var gone = await api.MetaAsync(discarded);
        journal.Check(gone.Status == 404 && gone.ErrorCode == "unknown_media_id",
            $"{discarded}: a discarded id is no longer a record (SERVER_SPEC.md § 11.3)", Explain(gone));

        return after;
    }

    private async Task<Snapshot?> SpecialAsync(Snapshot before)
    {
        journal.Step("special");

        if (before.PairToken is null)
        {
            journal.Note("the session is exhausted, so there is no pair to mark special");
            return null;
        }

        var marked = before.Right!.Id;

        var reply = await api.SpecialAsync(before.PairToken, "right", "rm2ctl-special-1");
        if (!journal.Check(reply.Status == 200, "POST /session/special (SERVER_SPEC.md § 10.9)", Explain(reply)))
            return null;

        var after = Snapshot.From(reply);
        if (after is null) return null;

        journal.Detail(after.Describe());
        journal.Note($"marked {marked} special — it moves to '<folder>/special 1/'");

        journal.Check(after.LastActionType == "special" && after.LastActionField("id") == marked,
            "lastAction distinguishes special from discard (SERVER_SPEC.md § 9.4)");

        journal.Check(after.Total == before.Total - 1,
            "special removes the record too (SERVER_SPEC.md § 10.9)",
            $"total {before.Total} -> {after.Total}");

        return after;
    }

    /// <summary>
    /// § 10.10 as it now stands: cancel takes back the last action of any kind, including a vote.
    /// This is the phone's cancel button, and the property that matters is not that it works but
    /// that it is exact — the pair comes back, the vote count goes back, and the token changes so a
    /// retry of the cancelled vote cannot slip in behind it.
    /// </summary>
    private async Task<Snapshot?> CancelVoteAsync(Snapshot before)
    {
        journal.Step("cancel the vote");

        if (!before.UndoAvailable)
        {
            journal.Note("nothing to cancel; skipping");
            return null;
        }

        var staleToken = before.PairToken;

        var reply = await api.UndoAsync("rm2ctl-cancel-1");
        if (!journal.Check(reply.Status == 200, "POST /session/undo cancels a vote (SERVER_SPEC.md § 10.10)", Explain(reply)))
            return null;

        var after = Snapshot.From(reply);
        if (after is null) return null;

        journal.Detail(after.Describe());

        journal.Check(after.LastActionType == "undo" && after.LastActionField("undoneType") == "vote",
            "lastAction.undoneType names what was cancelled (SERVER_SPEC.md § 9.4)",
            $"undoneType {after.LastActionField("undoneType")}");

        journal.Check(after.SessionVotes == before.SessionVotes - 1,
            "a cancelled vote is no longer counted (SERVER_SPEC.md § 10.10)",
            $"sessionVotes {before.SessionVotes} -> {after.SessionVotes}");

        journal.Check(after.Total == before.Total,
            "cancelling a vote moves no file and drops no record (SERVER_SPEC.md § 10.10)",
            $"total {before.Total} -> {after.Total}");

        journal.Check(after.PairSeq == before.PairSeq + 1,
            "pairSeq still advances, even though the pair went back (SERVER_SPEC.md § 8.3)",
            $"{before.PairSeq} -> {after.PairSeq}");

        journal.Check(after.PairToken is not null && after.PairToken != staleToken,
            "the restored pair carries a NEW token, so a vote still in flight cannot apply twice " +
            "(SERVER_SPEC.md § 10.10)");

        journal.Check(!after.UndoAvailable,
            "one level: the cancel is spent (SERVER_SPEC.md § 10.10)");

        if (staleToken is not null)
        {
            var replay = await api.VoteAsync(staleToken, "left", "rm2ctl-cancel-replay");
            journal.Check(replay.Status == 409 && replay.ErrorCode == "stale_pair_token",
                "replaying the cancelled vote is refused rather than re-applied (SERVER_SPEC.md § 13.3)",
                Explain(replay));
        }

        var second = await api.UndoAsync("rm2ctl-cancel-2");
        journal.Check(second.Status == 409 && second.ErrorCode == "nothing_to_undo",
            "a second cancel finds nothing — one level, no stack (SERVER_SPEC.md § 10.10)",
            Explain(second));

        return after;
    }

    private async Task<Snapshot?> UndoAsync(Snapshot before)
    {
        journal.Step("undo");

        if (!before.UndoAvailable)
        {
            journal.Note("nothing to undo; skipping");
            return null;
        }

        // § 10.10: undo takes no pairToken, deliberately. The move it reverses happened in a
        // previous pair generation, so any token the client holds for it is stale by construction.
        var reply = await api.UndoAsync("rm2ctl-undo-1");
        if (!journal.Check(reply.Status == 200, "POST /session/undo (SERVER_SPEC.md § 10.10)", Explain(reply)))
            return null;

        var after = Snapshot.From(reply);
        if (after is null) return null;

        journal.Detail(after.Describe());

        var restored = after.LastActionField("restoredId");
        journal.Note($"restored {after.LastActionField("id")} as {restored}");

        journal.Check(after.PairSeq == before.PairSeq + 1,
            "an undo advances pairSeq by 1 (SERVER_SPEC.md § 8.3)", $"{before.PairSeq} -> {after.PairSeq}");

        journal.Check(after.Total == before.Total + 1,
            "an undo puts the record back (SERVER_SPEC.md § 10.10)", $"total {before.Total} -> {after.Total}");

        journal.Check(after.LastActionType == "undo" && after.LastActionField("pairToken") is null,
            "lastAction.pairToken is null for an undo, which consumes no token (SERVER_SPEC.md § 9.4)");

        journal.Check(restored is not null,
            "undo reports restoredId — the name the file actually came back as, which differs from the " +
            "original when the name had been taken (SERVER_SPEC.md § 10.10)");

        journal.Check(!after.UndoAvailable,
            "one level, no stack: after an undo there is nothing left to undo (SERVER_SPEC.md § 10.10)");

        journal.Check(after.LastActionField("undoneType") is "discard" or "special",
            "lastAction.undoneType names the move that was cancelled (SERVER_SPEC.md § 9.4)",
            $"undoneType {after.LastActionField("undoneType")}");

        journal.Check(after.PairToken != before.PairToken,
            "undo always replaces the current pair, even a perfectly valid one (SERVER_SPEC.md § 7.4.4)");

        var second = await api.UndoAsync("rm2ctl-undo-2");
        journal.Check(second.Status == 409 && second.ErrorCode == "nothing_to_undo",
            "a second undo is 409 nothing_to_undo — it cannot reach back two actions (SERVER_SPEC.md § 13.3)",
            Explain(second));

        return after;
    }

    private async Task SaveAsync(Snapshot before)
    {
        journal.Step("save");

        var reply = await api.SaveAsync();
        if (!journal.Check(reply.Status == 200, "POST /session/save (SERVER_SPEC.md § 10.5)", Explain(reply)))
            return;

        var after = Snapshot.From(reply);
        if (after is null) return;

        journal.Detail($"lastSavedAt {after.LastSavedAt}");

        journal.Check(after.PairSeq == before.PairSeq && after.PairToken == before.PairToken,
            "save never changes the pair, the token or pairSeq (SERVER_SPEC.md § 10.5)",
            $"pairSeq {before.PairSeq} -> {after.PairSeq}");

        journal.Check(after.LastSavedAt is not null, "save updates lastSavedAt (SERVER_SPEC.md § 10.5)");

        var again = await api.SaveAsync();
        journal.Check(again.Status == 200, "save is idempotent and always safe to retry (SERVER_SPEC.md § 10.5)",
            Explain(again));
    }

    // ---- the refusals ---------------------------------------------------------------------------

    private async Task UnhappyPathsAsync(Snapshot snapshot)
    {
        journal.Title("The refusals the contract specifies");

        // --- a stale token -----------------------------------------------------------------
        journal.Step("stale pairToken");
        if (snapshot.PairToken is not null)
        {
            var current = await api.GetSessionAsync();
            var before = Snapshot.From(current);

            var reply = await api.VoteAsync("a-token-that-was-never-current", "left", "rm2ctl-stale");
            var ok = journal.Check(reply.Status == 409 && reply.ErrorCode == "stale_pair_token",
                "a token that is not the current one is 409 stale_pair_token (SERVER_SPEC.md § 8.4)",
                Explain(reply));

            if (ok)
            {
                // § 8.5: the 409 embeds the complete snapshot, so the client resynchronises in one
                // round trip and never has to poll.
                var embedded = Snapshot.FromError(reply);
                journal.Check(embedded is not null,
                    "a 409 on a /session* endpoint embeds the full SessionSnapshot at error.session — this is " +
                    "what stops a client polling after a conflict (SERVER_SPEC.md § 4, § 8.5)");

                journal.Check(reply.ErrorDetails?.TryGetProperty("suppliedToken", out _) == true &&
                              reply.ErrorDetails?.TryGetProperty("currentToken", out _) == true,
                    "the details carry suppliedToken and currentToken (SERVER_SPEC.md § 5.4)");

                var after = Snapshot.From(await api.GetSessionAsync());
                journal.Check(before is not null && after is not null &&
                              after.PairSeq == before.PairSeq &&
                              after.SessionVotes == before.SessionVotes &&
                              after.PairToken == before.PairToken,
                    "a stale token changes nothing at all: no save, no move, no impression, no advance " +
                    "(SERVER_SPEC.md § 8.5)",
                    before is null || after is null ? null
                        : $"pairSeq {before.PairSeq} -> {after.PairSeq}, " +
                          $"votes {before.SessionVotes} -> {after.SessionVotes}");
            }
        }

        // --- an unknown media id ------------------------------------------------------------
        journal.Step("unknown media id");
        var unknown = await api.MetaAsync("no-such-file-was-ever-scanned.jpg");
        journal.Check(unknown.Status == 404 && unknown.ErrorCode == "unknown_media_id",
            "an id that is not a record in the open session is 404 unknown_media_id (SERVER_SPEC.md § 11.2)",
            Explain(unknown));

        // --- a forbidden width --------------------------------------------------------------
        journal.Step("forbidden still width");
        if (snapshot.Left is { Kind: "still" } still)
        {
            var reply = await api.StillAsync(still.Id, width: 800);
            var ok = journal.Check(reply.Status == 400 && reply.ErrorCode == "unsupported_width",
                "a width outside {360, 540, 720, 1080, 1440, 2160} is 400 unsupported_width — capping the set " +
                "is what keeps the disk cache bounded (SERVER_SPEC.md § 12.3)",
                Explain(reply));

            if (ok && reply.ErrorDetails is { } details)
            {
                var allowed = details.TryGetProperty("allowed", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(w => w.GetInt32()).ToArray()
                    : Array.Empty<int>();

                journal.Check(allowed.SequenceEqual(new[] { 360, 540, 720, 1080, 1440, 2160 }),
                    "the refusal names the widths that would work (SERVER_SPEC.md § 5.2)",
                    $"details.allowed was [{string.Join(", ", allowed)}]");
            }
        }
        else
        {
            journal.Note("the current pair holds no still, so the width check is not applicable here");
        }

        // --- HEAD carries no body -----------------------------------------------------------
        // Worth checking over a real socket: an in-process test host does no HTTP framing, so this
        // is the only place the answer means anything (SERVER_SPEC.md § 2).
        journal.Step("HEAD on a media endpoint");
        if (snapshot.Left is { } reference)
        {
            var path = $"/media/{Rm2Api.EncodeId(reference.Id)}/meta";
            var get = await api.GetAsync(path, quiet: true);
            var head = await api.HeadAsync(path);

            journal.Check(head.Status == get.Status,
                "HEAD returns the identical status line to GET (SERVER_SPEC.md § 2)",
                $"GET was {get.Status}, HEAD was {head.Status}");

            journal.Check(head.Body.Length == 0,
                "HEAD returns no body (SERVER_SPEC.md § 2)",
                $"HEAD returned {head.Body.Length} bytes");
        }

        // --- unauthenticated -----------------------------------------------------------------
        journal.Step("unauthenticated");
        var token = api.Token;
        api.Token = null;
        try
        {
            var reply = await api.GetSessionAsync();
            journal.Check(reply.Status == 401 && reply.ErrorCode == "unauthenticated",
                "a request with no Authorization header is 401 unauthenticated — this check is the only thing " +
                "between the LAN and the filesystem (SERVER_SPEC.md § 3)",
                Explain(reply));

            if (token is not null)
            {
                // § 3: a token in a query string MUST be ignored, so it never lands in a log.
                var smuggled = await api.GetAsync($"/session?token={Uri.EscapeDataString(token)}");
                journal.Check(smuggled.Status == 401,
                    "a token in the query string does not authenticate (SERVER_SPEC.md § 3)",
                    Explain(smuggled));
            }

            var ping = await api.PingAsync(authenticate: false);
            journal.Check(ping.Status == 200,
                "GET /ping works without a token and returns the public subset (SERVER_SPEC.md § 3)",
                Explain(ping));

            if (ping.Json is { } body && body.TryGetProperty("session", out var session))
                journal.Check(session.ValueKind == JsonValueKind.Null,
                    "the public ping hides the session — an unauthenticated caller must not learn which folder " +
                    "is open (SERVER_SPEC.md § 14)");
        }
        finally
        {
            api.Token = token;
        }

        // --- no such route --------------------------------------------------------------------
        journal.Step("unknown route, and the rename routes that do not exist");
        var missing = await api.GetAsync("/no/such/route");
        journal.Check(missing.ErrorCode is not null,
            "an unknown route answers in the one error envelope (SERVER_SPEC.md § 4)", Explain(missing));

        // /session/rename is now a real route (SERVER_SPEC.md § 10.16, reversed 2026-09-16) and is
        // exercised for real by RenameAsync below. The general rule still stands: no *other* path
        // containing "rename" is a route.
        foreach (var path in new[] { "/rename", "/library/rename-by-rank" })
        {
            var reply = await api.GetAsync(path, quiet: true);
            journal.Check(reply.Status is 404 or 401 or 405,
                $"{path} is not a route — SERVER_SPEC.md § 1.1 still forbids any *other* rename path",
                $"answered {reply.Status}");
        }
    }

    private async Task CloseAsync()
    {
        journal.Title("Close");
        journal.Step("close the session");

        var reply = await api.CloseSessionAsync();
        journal.Check(reply.Status == 204, "DELETE /session is 204 (SERVER_SPEC.md § 10.4)", Explain(reply));

        var again = await api.CloseSessionAsync();
        journal.Check(again.Status == 404 && again.ErrorCode == "no_session",
            "a repeated DELETE is 404 no_session, which a client must not treat as a failure " +
            "(SERVER_SPEC.md § 10.4)",
            Explain(again));

        // --- no session ------------------------------------------------------------------------
        journal.Step("no session open");
        var session = await api.GetSessionAsync();
        journal.Check(session.Status == 404 && session.ErrorCode == "no_session",
            "every /session* call with none open is 404 no_session (SERVER_SPEC.md § 7.1)", Explain(session));

        var media = await api.MetaAsync("anything.jpg");
        journal.Check(media.Status == 404 && media.ErrorCode == "no_session",
            "there is no way to fetch bytes without a session (SERVER_SPEC.md § 11.2 step 1)", Explain(media));
    }

    /// <summary>What actually came back, for a failure message.</summary>
    private static string Explain(Reply reply) =>
        $"{reply.Method} {reply.Url}\n" +
        $"answered {reply.Status}" +
        (reply.ErrorCode is { } code ? $" {code}" : "") +
        (reply.ErrorMessage is { } message ? $" — {message}" : "") +
        (reply.ErrorCode is null && reply.Body.Length > 0 && reply.Status >= 400
            ? "\nbody: " + Journal.Trim(reply.Text.ReplaceLineEndings(" "), 300)
            : "");
}
