using System.Globalization;
using System.Text.Json;
using RankMaster2.Catalog;
using RankMaster2.Ranking;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// The one session, server-wide (SERVER_SPEC.md § 7). Holds the <see cref="RankingSession"/>, the
/// exclusive folder lock, the semaphore, the per-session secret, <c>pairSeq</c> and
/// <c>lastAction</c>.
///
/// Two rules shape everything below.
///
/// **Serialisation (§ 7.3).** <see cref="RankingSession"/> is not thread-safe and its <c>Find</c>
/// throws on a miss, so every mutating call *and* every call that reads a snapshot holds the
/// semaphore, and the snapshot is materialised inside it. Two concurrent requests can then never
/// observe a half-applied action. Five seconds without the semaphore is <c>503 session_busy</c>.
///
/// **The failure semantics are not symmetric (§ 8.3).** Vote and skip save inside the library call
/// and roll the whole in-memory state back if that save throws, so on their failure path nothing
/// changed and the client's token is still valid. Discard, special and undo move the file *first*
/// and then call <c>Drop</c>/<c>Restore</c>, neither of which saves or can roll back, so a save
/// that throws after the move leaves the change committed and <c>pairSeq</c> advanced. Same status
/// code, opposite meaning. The normative table is § 8.3 and this class follows it exactly.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    public const int DefaultPrefetchPairs = 2;
    public const string ApiBase = "/api/v1";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Paths compare the way the filesystem does (§ 10.1).</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICatalog _catalog;
    private readonly IRatingEngine _engine;
    private readonly IPairSelector _selector;
    private readonly IMediaPipeline _pipeline = new NoOpMediaPipeline();
    private readonly IRenameJournalWriter _journalWriter;
    private readonly int _prefetchPairs;
    private readonly string _serverVersion;

    private OpenSession? _open;
    private RenameRun? _rename;
    private bool _disposed;

    public SessionRegistry(
        ICatalog? catalog = null,
        IRatingEngine? engine = null,
        IPairSelector? selector = null,
        int prefetchPairs = DefaultPrefetchPairs,
        string? serverVersion = null,
        IRenameJournalWriter? journalWriter = null)
    {
        _catalog = catalog ?? new JsonCatalog();
        _engine = engine ?? new TrueSkill();
        _selector = selector ?? new PairSelector();
        _prefetchPairs = prefetchPairs;
        _serverVersion = serverVersion
            ?? typeof(SessionRegistry).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        _journalWriter = journalWriter ?? new DefaultRenameJournalWriter();
    }

    /// <summary>
    /// The process-wide session. Exactly one exists by contract (§ 1.1), so the media layer can
    /// read this rather than plumbing the instance through every call site. A test may construct
    /// its own registry and register it in DI instead; <c>MapSessionEndpoints</c> prefers DI.
    /// </summary>
    public static SessionRegistry Shared { get; } = new();

    /// <summary>
    /// Hook for the media layer: called with the id about to be moved, before the move, so any
    /// server-side decode of it can be released first (§ 10.8, "release any server-side decode").
    /// Left null here because this folder owns no decoders.
    /// </summary>
    public Action<MediaId>? ReleaseMedia { get; set; }

    /// <summary>The open folder, or null. A cheap read for <c>GET /ping</c>; not a substitute for a snapshot.</summary>
    public string? OpenFolder => Volatile.Read(ref _open)?.Folder;

    private RankMaster2.Server.Media.MediaSessionView? _mediaView;
    private OpenSession? _mediaViewOf;
    private ulong _mediaViewSeq;

    /// <summary>
    /// The read-only window the media layer needs (<see cref="Media.IMediaSessionAccessor"/>).
    /// <para/>
    /// Media requests are GETs and SERVER_SPEC.md § 11.3 forbids them from mutating session state,
    /// so they must not queue behind a vote either. But <see cref="RankingSession.Records"/> hands
    /// back its live backing list, and copying it while a vote commits throws
    /// "collection was modified". So: read optimistically, then confirm nothing moved underneath,
    /// and cache the result against <c>PairSeq</c> — which every mutation bumps — so the common
    /// case costs a reference comparison rather than rebuilding a dictionary per image.
    /// <para/>
    /// Only if that keeps losing the race do we take the gate and pay the wait.
    /// </summary>
    public Media.IMediaSessionView? CurrentForMedia
    {
        get
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var open = Volatile.Read(ref _open);
                if (open is null)
                    return null;

                var before = open.PairSeq;
                if (ReferenceEquals(Volatile.Read(ref _mediaViewOf), open) &&
                    Volatile.Read(ref _mediaViewSeq) == before &&
                    Volatile.Read(ref _mediaView) is { } cached)
                    return cached;

                Media.MediaSessionView built;
                try
                {
                    built = BuildMediaView(open);
                }
                catch (InvalidOperationException)
                {
                    continue;   // a mutation committed mid-copy; take a fresh look
                }

                // If the session advanced or closed while we were copying, the view we just built
                // may describe a pair that no longer exists. Drop it rather than cache it.
                if (open.PairSeq != before || !ReferenceEquals(Volatile.Read(ref _open), open))
                    continue;

                Volatile.Write(ref _mediaViewOf, open);
                Volatile.Write(ref _mediaViewSeq, before);
                Volatile.Write(ref _mediaView, built);
                return built;
            }

            // Persistently contended. Correctness over latency: wait for the gate.
            if (!_gate.Wait(LockTimeout))
                throw new TimeoutException("Timed out reading session state for a media request.");
            try
            {
                var open = _open;
                return open is null ? null : BuildMediaView(open);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static Media.MediaSessionView BuildMediaView(OpenSession open)
    {
        // § 11.2 step 4: membership is against Records - videos in a mixed folder are served even
        // though they are not rankable, and anything discarded is already gone from it.
        var records = open.Session.Records.ToArray();
        return new Media.MediaSessionView(
            open.Folder,
            MediaExtensions.RankPolicy(records.Select(r => r.Kind)),
            records);
    }

    // ---------------------------------------------------------------------------------------
    // POST /session — § 10.1
    // ---------------------------------------------------------------------------------------

    public async Task<SessionOutcome> OpenAsync(JsonElement? body, CancellationToken cancellation)
    {
        var error = SessionBody.RequiredString(body, "folder", out var raw);
        if (error is not null)
            return SessionOutcome.Fail(error.Code, error.Message, error.Details);

        var pathError = ResolveFolder(raw, out var folder);
        if (pathError is not null)
            return SessionOutcome.Fail(pathError.Code, pathError.Message, pathError.Details);

        return await WithLockAsync(cancellation, () =>
        {
            // Step 2. A session on the same folder is a pure read: Start() must not run again,
            // because it clears the cues, SessionVotes and the recent-shown set of a phone that is
            // merely resuming. pairSeq does not move.
            if (_open is { } open)
            {
                if (string.Equals(open.Folder, folder, PathComparison))
                    return SessionOutcome.Ok(Materialise(open));

                return SessionOutcome.Fail(
                    ErrorCodes.SessionAlreadyOpen,
                    "A session is already open on another folder.",
                    new { openFolder = open.Folder },
                    Materialise(open));
            }

            // Step 3.
            if (!Directory.Exists(folder))
            {
                if (File.Exists(folder))
                {
                    return SessionOutcome.Fail(
                        ErrorCodes.FolderNotADirectory,
                        "That path is a file, not a folder.",
                        new { folder });
                }

                return SessionOutcome.Fail(
                    ErrorCodes.FolderNotFound,
                    "That folder does not exist.",
                    new { folder });
            }

            try
            {
                using var probe = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
                probe.MoveNext();
            }
            catch (UnauthorizedAccessException)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.FolderAccessDenied,
                    "That folder could not be read.",
                    new { folder });
            }
            catch (IOException)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.FolderAccessDenied,
                    "That folder could not be read.",
                    new { folder });
            }

            // Step 4.
            var folderLock = FolderLock.TryAcquire(folder, _serverVersion, out var holder);
            if (folderLock is null)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.FolderLocked,
                    "That folder is in use by another Rank Master process.",
                    new
                    {
                        holder = holder is null
                            ? null
                            : new
                            {
                                pid = holder.Pid,
                                host = holder.Host,
                                startedAt = holder.StartedAt,
                                process = holder.Process,
                            },
                    });
            }

            // Step 4.5 (§ 10.16): recovery runs before anything scans. If a rename was interrupted,
            // <folder>/.rankmaster-rename.json is still here; reunite every rating with its file at
            // that file's current name before RankingSession.Start() ever calls JsonCatalog.Scan —
            // the plain Scan/Save merge is exactly the thing that would silently drop them (§ 1).
            if (RenameEngine.JournalExists(folder))
            {
                var recovery = RenameEngine.RecoverIfPresent(folder, _catalog);
                if (!recovery.Reunited)
                {
                    folderLock.Dispose();
                    return SessionOutcome.Fail(
                        ErrorCodes.RenameFailed,
                        "An interrupted rename could not be reunited with its ratings.",
                        new { reunited = false, journal = recovery.JournalPath });
                }
            }

            // Step 5. Every failure from here releases the lock before it answers.
            var ranking = new RankingSession(folder, _catalog, _engine, _selector, _prefetchPairs);
            bool started;
            try
            {
                started = ranking.Start();
            }
            catch (InvalidDataException)
            {
                folderLock.Dispose();
                return SessionOutcome.Fail(
                    ErrorCodes.LibraryJsonUnreadable,
                    "The ranking file is unreadable and was not overwritten.",
                    new { file = Path.Combine(folder, JsonCatalog.FileName) });
            }
            catch (UnauthorizedAccessException)
            {
                folderLock.Dispose();
                return SessionOutcome.Fail(
                    ErrorCodes.FolderAccessDenied,
                    "That folder could not be read.",
                    new { folder });
            }
            catch (DirectoryNotFoundException)
            {
                folderLock.Dispose();
                return SessionOutcome.Fail(
                    ErrorCodes.FolderNotFound,
                    "That folder does not exist.",
                    new { folder });
            }
            catch (Exception)
            {
                folderLock.Dispose();
                return SessionOutcome.Fail(
                    ErrorCodes.InternalError,
                    "The folder could not be opened.");
            }

            if (!started)
            {
                // Start() populated Records before it failed to pick, so the counts are real.
                var stills = ranking.Records.Count(r => r.Kind == MediaKind.Still);
                var videos = ranking.Records.Count(r => r.Kind == MediaKind.Video);
                var rankable = ranking.Rankable.Count;
                folderLock.Dispose();
                return SessionOutcome.Fail(
                    ErrorCodes.FolderNotRankable,
                    "This folder needs at least two supported media files.",
                    new { stills, videos, rankable });
            }

            var opened = new OpenSession(
                PairTokens.NewSessionId(),
                PairTokens.NewSecret(),
                folder,
                DateTimeOffset.UtcNow,
                ranking,
                folderLock,
                _prefetchPairs);

            opened.Actions = new LibraryActions(
                () => folder,
                _catalog,
                () => _pipeline,
                () => ranking,
                id => ReleaseMedia?.Invoke(id));

            _open = opened;

            // A freshly opened session starts with no rename recorded, even if one from an earlier
            // session on this same folder is still sitting here — GET/cancel of /session/rename
            // must answer 404 no_rename_operation until this session starts one of its own.
            Volatile.Write(ref _rename, null);

            // Start() does not save: lastSavedAt describes this session's writes, not the file's age.
            return SessionOutcome.Ok(Materialise(opened), StatusCodes.Status201Created);
        });
    }

    // ---------------------------------------------------------------------------------------
    // GET /session, GET /session/pair — § 10.2, § 10.3. Pure reads; no impression, no advance.
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> ReadAsync(CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
            _open is { } open ? SessionOutcome.Ok(Materialise(open)) : NoSession());

    // ---------------------------------------------------------------------------------------
    // DELETE /session — § 10.4. Writes nothing.
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> CloseAsync(CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
        {
            if (_open is not { } open)
                return NoSession();

            _open = null;
            Volatile.Write(ref _rename, null);
            open.Lock.Dispose();
            return SessionOutcome.NoContent();
        });

    // ---------------------------------------------------------------------------------------
    // POST /session/save — § 10.5. Allowed while exhausted; never touches the pair or pairSeq.
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> SaveAsync(CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
        {
            if (_open is not { } open)
                return NoSession();

            if (open.RenameInProgress)
                return RenameInProgress(open);

            try
            {
                open.Session.Save();
            }
            catch (Exception)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The ranking file could not be written.",
                    new { recordsChanged = false, fileMoved = false },
                    Materialise(open));
            }

            open.LastSavedAt = DateTimeOffset.UtcNow;
            return SessionOutcome.Ok(Materialise(open));
        });

    // ---------------------------------------------------------------------------------------
    // POST /session/vote — § 10.6
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> VoteAsync(JsonElement? body, BodyError? bodyError, CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
        {
            if (_open is not { } open)
                return NoSession();

            if (open.RenameInProgress)
                return RenameInProgress(open);

            // § 8.4 step 1 before step 2: a body that never parsed is still a step-2 failure, so
            // with no session open the caller hears "reopen", not "your body is bad" — otherwise a
            // client that branches on the code retries the body forever against a closed session.
            if (bodyError is not null)
                return BodyFail(bodyError, open);

            // All three are read before any is reported, so `out` stays definitely assigned; the
            // first failure in body order is the one that answers.
            var tokenError = SessionBody.RequiredString(body, "pairToken", out var token);
            var winnerError = SessionBody.RequiredSide(body, "winner", out var winner);
            var idError = SessionBody.OptionalClientRequestId(body, out var clientRequestId);
            if ((tokenError ?? winnerError ?? idError) is { } error)
                return BodyFail(error, open);

            var conflict = CheckPair(open, token);
            if (conflict is not null)
                return conflict;

            try
            {
                if (winner == Sides.Left)
                    open.Session.VoteLeft();
                else
                    open.Session.VoteRight();
            }
            catch (Exception)
            {
                // § 7.4.5: ApplyVote restored records, Current, SessionVotes, the warm queue, the
                // recent set and the cue. Nothing changed, so pairSeq does not move and the token
                // the client holds is still the current one — the identical request may be retried.
                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The ranking file could not be written; the vote was rolled back.",
                    new { recordsChanged = false, fileMoved = false },
                    Materialise(open));
            }

            open.PairSeq++;
            open.LastSavedAt = DateTimeOffset.UtcNow;
            open.LastAction = NewAction(open, ActionTypes.Vote, token, clientRequestId, winner: winner);
            return SessionOutcome.Ok(Materialise(open));
        });

    // ---------------------------------------------------------------------------------------
    // POST /session/skip — § 10.7. Same failure semantics as vote.
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> SkipAsync(JsonElement? body, BodyError? bodyError, CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
        {
            if (_open is not { } open)
                return NoSession();

            if (open.RenameInProgress)
                return RenameInProgress(open);

            // § 8.4 step 1 before step 2: a body that never parsed is still a step-2 failure, so
            // with no session open the caller hears "reopen", not "your body is bad" — otherwise a
            // client that branches on the code retries the body forever against a closed session.
            if (bodyError is not null)
                return BodyFail(bodyError, open);

            var tokenError = SessionBody.RequiredString(body, "pairToken", out var token);
            var idError = SessionBody.OptionalClientRequestId(body, out var clientRequestId);
            if ((tokenError ?? idError) is { } error)
                return BodyFail(error, open);

            var conflict = CheckPair(open, token);
            if (conflict is not null)
                return conflict;

            try
            {
                open.Session.Skip();
            }
            catch (Exception)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The ranking file could not be written; the skip was rolled back.",
                    new { recordsChanged = false, fileMoved = false },
                    Materialise(open));
            }

            open.PairSeq++;
            open.LastSavedAt = DateTimeOffset.UtcNow;
            open.LastAction = NewAction(open, ActionTypes.Skip, token, clientRequestId);
            return SessionOutcome.Ok(Materialise(open));
        });

    // ---------------------------------------------------------------------------------------
    // POST /session/discard, POST /session/special — § 10.8, § 10.9
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> MoveAsync(JsonElement? body, BodyError? bodyError, bool special, CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
        {
            if (_open is not { } open)
                return NoSession();

            if (open.RenameInProgress)
                return RenameInProgress(open);

            // § 8.4 step 1 before step 2: a body that never parsed is still a step-2 failure, so
            // with no session open the caller hears "reopen", not "your body is bad" — otherwise a
            // client that branches on the code retries the body forever against a closed session.
            if (bodyError is not null)
                return BodyFail(bodyError, open);

            var tokenError = SessionBody.RequiredString(body, "pairToken", out var token);
            var sideError = SessionBody.RequiredSide(body, "side", out var side);
            var idError = SessionBody.OptionalClientRequestId(body, out var clientRequestId);
            if ((tokenError ?? sideError ?? idError) is { } error)
                return BodyFail(error, open);

            var conflict = CheckPair(open, token);
            if (conflict is not null)
                return conflict;

            // The id comes from the pair the token names; the client never sends one, so it cannot
            // act on an item that is not on screen (§ 10.8).
            var current = open.Session.Current!.Value;
            var id = side == Sides.Left ? current.Left : current.Right;
            var actionType = special ? ActionTypes.Special : ActionTypes.Discard;

            // The missing-file special case (§ 10.8). FileOps.MoveToSubfolder throws
            // FileNotFoundException on a vanished source, which would wedge the pair forever: it
            // cannot be voted (the media will not load) and it cannot be discarded. So drop the
            // record and save directly, and record no undo entry — there is no file to put back,
            // and undoAvailable is left exactly as it was.
            if (!File.Exists(Path.Combine(open.Folder, id.Filename)))
            {
                open.Session.Drop(id);
                open.PairSeq++;

                try
                {
                    open.Session.Save();
                }
                catch (Exception)
                {
                    open.LastAction = NewAction(
                        open, ActionTypes.DropMissing, pairToken: null, clientRequestId, id: id.Filename);
                    return SessionOutcome.Fail(
                        ErrorCodes.SaveFailed,
                        "The record was dropped but the ranking file could not be written.",
                        new { recordsChanged = true, fileMoved = false },
                        Materialise(open));
                }

                open.LastSavedAt = DateTimeOffset.UtcNow;
                open.LastAction = NewAction(
                    open, ActionTypes.DropMissing, pairToken: null, clientRequestId, id: id.Filename);
                return SessionOutcome.Ok(Materialise(open));
            }

            try
            {
                if (special)
                    open.Actions.MoveToSpecial(id);
                else
                    open.Actions.Discard(id);
            }
            catch (Exception)
            {
                // LibraryActions.Move runs move -> Drop -> Save. Drop is what tells the two failure
                // stages apart: if the record is still there, Drop never ran, so the move threw and
                // nothing changed. If it is gone, the file is already in discarded/ or special 1/
                // and it was the save that threw — Drop does not save and cannot roll back, so that
                // change is committed and pairSeq has to advance (§ 8.3).
                if (open.Session.TryFind(id, out _))
                {
                    return SessionOutcome.Fail(
                        ErrorCodes.MoveFailed,
                        "The file could not be moved.",
                        new { id = id.Filename, stage = "move" },
                        Materialise(open));
                }

                open.PairSeq++;
                open.LastAction = NewAction(open, actionType, token, clientRequestId, side: side, id: id.Filename);
                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The file was moved but the ranking file could not be written.",
                    new { recordsChanged = true, fileMoved = true },
                    Materialise(open));
            }

            open.PairSeq++;
            open.LastSavedAt = DateTimeOffset.UtcNow;
            open.LastAction = NewAction(open, actionType, token, clientRequestId, side: side, id: id.Filename);
            return SessionOutcome.Ok(Materialise(open));
        });

    // ---------------------------------------------------------------------------------------
    // POST /session/undo — § 10.10. Takes no pairToken; one level, no stack; not vote undo.
    // ---------------------------------------------------------------------------------------

    public Task<SessionOutcome> UndoAsync(JsonElement? body, BodyError? bodyError, CancellationToken cancellation) =>
        WithLockAsync(cancellation, () =>
        {
            if (_open is not { } open)
                return NoSession();

            if (open.RenameInProgress)
                return RenameInProgress(open);

            // § 8.4 step 1 before step 2: a body that never parsed is still a step-2 failure, so
            // with no session open the caller hears "reopen", not "your body is bad" — otherwise a
            // client that branches on the code retries the body forever against a closed session.
            if (bodyError is not null)
                return BodyFail(bodyError, open);

            var error = SessionBody.OptionalClientRequestId(body, out var clientRequestId);
            if (error is not null)
                return BodyFail(error, open);

            // § 10.10. A vote or skip is the most recent action whenever the engine still holds an
            // undo point: any discard, special or drop clears it. So this branch is "cancel the
            // action", and the one below is "cancel the move", and they can never both apply.
            if (open.Session.CanUndoLastAction)
                return UndoAction(open, clientRequestId);

            if (open.Actions.LastMove is not { } move)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.NothingToUndo,
                    "There is no move to undo.",
                    null,
                    Materialise(open));
            }

            if (!string.Equals(move.Folder, open.Folder, PathComparison))
            {
                open.Actions.ClearLastMove();
                return SessionOutcome.Fail(
                    ErrorCodes.UndoFolderChanged,
                    "The recorded move belongs to a different folder.",
                    new { moveFolder = move.Folder },
                    Materialise(open));
            }

            var before = open.Session.Records.Select(r => r.Id).ToHashSet();

            // § 9.4 undoneType: which action this cancel is taking back. Read before the undo, since
            // LastAction is about to be replaced by the undo itself.
            var undoneMove = open.LastAction?.Type == ActionTypes.Special
                ? ActionTypes.Special
                : ActionTypes.Discard;

            bool undone;
            try
            {
                undone = open.Actions.UndoLastMove();
            }
            catch (Exception)
            {
                // The move back runs before Restore, and Restore does not save and cannot roll
                // back, so whether the record came back is what separates "nothing changed" from
                // "committed" (§ 8.3, the two undo rows). The records are the only honest witness:
                // an empty discarded/ also leaves DestPath missing, and reading that as "the file
                // made it home" reported a committed restore for a file nobody will ever see again.
                if (open.Session.Records.Select(r => r.Id).ToHashSet().SetEquals(before))
                {
                    return SessionOutcome.Fail(
                        ErrorCodes.MoveFailed,
                        "The file could not be moved back.",
                        new { id = move.OriginalName, stage = "undo-move" },
                        Materialise(open));
                }

                // The record is restored, so the move back ran; it was the save that threw.
                // LibraryActions clears LastMove only after that save, so clear it here: the move
                // has been reversed, and leaving it recorded would offer an undo that can only fail.
                open.Actions.ClearLastMove();
                open.PairSeq++;
                open.LastAction = NewAction(
                    open,
                    ActionTypes.Undo,
                    pairToken: null,
                    clientRequestId,
                    id: move.OriginalName,
                    restoredId: RestoredId(open, before, move.OriginalName),
                    undoneType: undoneMove);
                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The file was moved back but the ranking file could not be written.",
                    new { recordsChanged = true, fileMoved = true },
                    Materialise(open));
            }

            if (!undone)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.NothingToUndo,
                    "There is no move to undo.",
                    null,
                    Materialise(open));
            }

            open.PairSeq++;
            open.LastSavedAt = DateTimeOffset.UtcNow;
            open.LastAction = NewAction(
                open,
                ActionTypes.Undo,
                pairToken: null,
                clientRequestId,
                id: move.OriginalName,
                restoredId: RestoredId(open, before, move.OriginalName),
                undoneType: undoneMove);
            return SessionOutcome.Ok(Materialise(open));
        });

    // ---------------------------------------------------------------------------------------
    // POST /session/rename, GET /session/rename, POST /session/rename/cancel — § 10.16.
    //
    // The first long-running operation in the contract (§ 13.1's named exception): a 202 is an
    // acknowledgement, not a completion. The run holds the session semaphore only twice — here, to
    // create the operation, build the plan and write+fsync the journal before returning 202; and at
    // the very end, to apply the result to the session. Everything in between (the moves, the
    // commit) runs on a background task, off the lock, so a vote elsewhere in the app is never
    // blocked behind a rename — it is refused outright instead (`rename_in_progress`), because
    // nothing else may mutate the folder while the journal's plan is being carried out.
    // ---------------------------------------------------------------------------------------

    public async Task<RenameOutcome> StartRenameAsync(CancellationToken cancellation)
    {
        var acquired = await _gate.WaitAsync(LockTimeout, cancellation);
        if (!acquired)
        {
            return RenameOutcome.Fail(
                ErrorCodes.SessionBusy,
                "The session is busy; another action is still running.",
                new { retryAfterSeconds = 1 });
        }

        OpenSession open;
        RenameRun run;
        bool abortedBeforeAnyMove;
        try
        {
            if (_open is not { } o)
                return RenameOutcome.Fail(ErrorCodes.NoSession, "No session is open.");
            open = o;

            if (open.RenameInProgress)
            {
                var existing = Volatile.Read(ref _rename);
                return RenameOutcome.Fail(
                    ErrorCodes.RenameInProgress,
                    "A rename is already running.",
                    new { operationId = existing?.OperationId },
                    Materialise(open));
            }

            // § 3.3 "preparing": order by μ − 3σ desc, then filename, from the records as they are
            // right now. Published to _rename before the (possibly slow, or test-blocked) journal
            // write, so a concurrent cancel can flag it even while this call has not returned.
            var plan = RenameEngine.BuildPlan(open.Session.Records);
            run = new RenameRun(PairTokens.NewSessionId(), open.Folder, open.Session.Records, DateTimeOffset.UtcNow)
            {
                Plan = plan,
            };
            open.RenameInProgress = true;
            Volatile.Write(ref _rename, run);

            _journalWriter.Write(open.Folder, plan, DateTimeOffset.UtcNow);

            abortedBeforeAnyMove = run.CancelRequested;
            if (abortedBeforeAnyMove)
            {
                // § 3.5, "preparing": abort cleanly before any move. Delete the half-written
                // journal (nothing has moved, so there is nothing else to undo) and leave the
                // session exactly as it was.
                RenameEngine.DeleteJournal(open.Folder);
                open.RenameInProgress = false;
                run.AbortBeforeAnyMove();
            }
            else
            {
                run.MarkPreparingDone(total: plan.Count * 2);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Captured before the background task is dispatched: for a small folder the run can reach
        // `succeeded` before this thread would otherwise get back from Task.Run, and the wire shape
        // (§ 3.4) promises 202 always answers with the just-started "running" state.
        var accepted = run.ToWire();

        if (!abortedBeforeAnyMove)
            _ = Task.Run(() => RunRenameAsync(open, run));

        return RenameOutcome.Ok(accepted, StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// § 3.4: reads the operation without the session lock, so the poll for the bar never queues
    /// behind the run it is watching.
    /// </summary>
    public Task<RenameOutcome> GetRenameAsync(CancellationToken cancellation)
    {
        if (Volatile.Read(ref _open) is null)
            return Task.FromResult(RenameOutcome.Fail(ErrorCodes.NoSession, "No session is open."));

        var run = Volatile.Read(ref _rename);
        if (run is null)
        {
            return Task.FromResult(RenameOutcome.Fail(
                ErrorCodes.NoRenameOperation, "No rename operation is recorded for this session."));
        }

        return Task.FromResult(RenameOutcome.Ok(run.ToWire()));
    }

    /// <summary>
    /// § 3.5: stop and reunite in place, never a database-risking rollback. Deliberately does not
    /// take the session semaphore — it only has to flag the run, so it must be able to land even
    /// while <see cref="StartRenameAsync"/> is still holding the gate inside the journal write.
    /// Idempotent: a second cancel just reports the state the first one produced.
    /// </summary>
    public Task<RenameOutcome> CancelRenameAsync(CancellationToken cancellation)
    {
        if (Volatile.Read(ref _open) is null)
            return Task.FromResult(RenameOutcome.Fail(ErrorCodes.NoSession, "No session is open."));

        var run = Volatile.Read(ref _rename);
        if (run is null)
        {
            return Task.FromResult(RenameOutcome.Fail(
                ErrorCodes.NoRenameOperation, "No rename operation is recorded for this session."));
        }

        run.RequestCancel();
        return Task.FromResult(RenameOutcome.Ok(run.ToWire()));
    }

    /// <summary>
    /// The live forward operation (§ 3.3), off the session lock: phase 1, phase 2, the commit, the
    /// journal delete (the commit point), then the apply back under the lock. Cancellation is
    /// polled between files; once phase 2 (renaming) has fully finished and the commit has begun,
    /// it is too late (§ 3.5) and the run always finishes to a terminus.
    /// </summary>
    private async Task RunRenameAsync(OpenSession open, RenameRun run)
    {
        var plan = run.Plan;

        try
        {
            RenameEngine.MovePhase1(
                open.Folder, plan,
                shouldStop: _ => run.CancelRequested,
                onProgress: (done, _) => run.ReportProgress(RenamePhases.Renaming, done));

            if (run.CancelRequested)
            {
                await CancelInPlaceAsync(open, run);
                return;
            }

            RenameEngine.MovePhase2(
                open.Folder, plan,
                shouldStop: _ => run.CancelRequested,
                onProgress: (done, _) => run.ReportProgress(RenamePhases.Renaming, plan.Count + done));

            if (run.CancelRequested)
            {
                await CancelInPlaceAsync(open, run);
                return;
            }

            run.ReportProgress(RenamePhases.Saving, plan.Count * 2);

            IReadOnlyList<MediaRecord> remapped;
            try
            {
                remapped = RenameEngine.Commit(_catalog, open.Folder, run.PreRenameRecords, plan);
            }
            catch (Exception)
            {
                // The commit threw. The journal is still on disk, so reunite from it right now
                // rather than waiting for the next open — the ratings are the asset (§ 1).
                var recovery = RenameEngine.RecoverIfPresent(open.Folder, _catalog);
                await FinishAsync(open, run, () =>
                {
                    open.RenameInProgress = false;
                    run.Fail(recovery.Reunited, recovery.JournalPath);
                });
                return;
            }

            RenameEngine.DeleteJournal(open.Folder);   // the commit point (§ 3.3 step 4)

            await FinishAsync(open, run, () =>
            {
                open.Session.ReplaceAll(remapped);
                open.Actions.ClearLastMove();
                open.PairSeq++;
                open.LastSavedAt = DateTimeOffset.UtcNow;
                open.LastAction = new SnapshotLastAction(
                    open.PairSeq, ActionTypes.Rename,
                    PairToken: null, ClientRequestId: null, Winner: null, Side: null,
                    Id: null, RestoredId: null, UndoneType: null, At: Rfc3339(DateTimeOffset.UtcNow));
                open.RenameInProgress = false;
                run.Succeed();
            });
        }
        catch (Exception)
        {
            // Anything else that went wrong mid-move: the journal is still on disk (it is only
            // deleted at the commit point above), so recovery from it is exactly the right answer,
            // identical to what the next POST /session would do if the process had died here.
            var recovery = RenameEngine.RecoverIfPresent(open.Folder, _catalog);
            await FinishAsync(open, run, () =>
            {
                open.RenameInProgress = false;
                run.Fail(recovery.Reunited, recovery.JournalPath);
            });
        }
    }

    /// <summary>§ 3.5: stop issuing moves, reunite in place (never finalizing the rest), resync the
    /// session from a fresh scan since filenames on disk may now differ from what it holds in
    /// memory.</summary>
    private async Task CancelInPlaceAsync(OpenSession open, RenameRun run)
    {
        run.ReportPhase(RenamePhases.Reuniting);
        var outcome = RenameEngine.ReuniteInPlace(open.Folder, _catalog);

        await FinishAsync(open, run, () =>
        {
            open.RenameInProgress = false;
            if (outcome.Reunited)
            {
                open.Session.Start();   // re-scan: the folder may be a mix of old and new names now
                run.Cancel();
            }
            else
            {
                run.Fail(outcome.Reunited, outcome.JournalPath);
            }
        });
    }

    /// <summary>
    /// Re-acquires the session gate to apply the run's result. § 3.4: the run is server-owned and
    /// reaches a consistent terminus even if the client vanished — including if the session itself
    /// was closed and possibly reopened while the run was off the lock, in which case there is
    /// nothing left in memory to apply to; the filesystem and database work already committed
    /// correctly regardless, so this is not a failure, just nothing further to do.
    /// </summary>
    private async Task FinishAsync(OpenSession open, RenameRun run, Action apply)
    {
        var acquired = await _gate.WaitAsync(LockTimeout);
        try
        {
            if (acquired && ReferenceEquals(_open, open))
                apply();
        }
        finally
        {
            if (acquired)
                _gate.Release();
        }
    }

    /// <summary>§ 3.6: the answer every mutating <c>/session*</c> call gives while a rename runs.</summary>
    private SessionOutcome RenameInProgress(OpenSession open) =>
        SessionOutcome.Fail(
            ErrorCodes.RenameInProgress,
            "A rename is already running.",
            new { operationId = Volatile.Read(ref _rename)?.OperationId },
            Materialise(open));

    // ---------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var open = _open;
        _open = null;
        open?.Lock.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// § 8.4, steps 3 and 4, in that exact order and inside the lock. A stale token is a pure read:
    /// no save, no move, no impression, no advance, no pairSeq bump, and the reply carries the
    /// complete snapshot so the client resynchronises in one round trip and never polls.
    /// </summary>
    private SessionOutcome? CheckPair(OpenSession open, string suppliedToken)
    {
        if (open.Session.Current is null)
        {
            return SessionOutcome.Fail(
                ErrorCodes.NoCurrentPair,
                "There is no current pair to act on.",
                null,
                Materialise(open));
        }

        var current = CurrentToken(open);
        if (!PairTokens.Matches(suppliedToken, current))
        {
            return SessionOutcome.Fail(
                ErrorCodes.StalePairToken,
                "The supplied pairToken is not the current pair.",
                new { suppliedToken, currentToken = current },
                Materialise(open));
        }

        return null;
    }

    private static string RestoredId(OpenSession open, HashSet<MediaId> before, string fallback)
    {
        foreach (var record in open.Session.Records)
        {
            if (!before.Contains(record.Id))
                return record.Id.Filename;
        }

        return fallback;
    }

    /// <summary>
    /// § 10.10 for a <c>vote</c> or <c>skip</c>: the engine restores the snapshot it took before
    /// that action and saves. Nothing moves on disk, so unlike cancelling a move this is
    /// all-or-nothing — a failed save leaves the action applied and the client's token valid.
    /// </summary>
    private SessionOutcome UndoAction(OpenSession open, string? clientRequestId)
    {
        var undoneType = open.LastAction?.Type switch
        {
            ActionTypes.Vote => ActionTypes.Vote,
            ActionTypes.Skip => ActionTypes.Skip,
            _ => ActionTypes.Vote,
        };

        try
        {
            if (!open.Session.UndoLastAction())
            {
                return SessionOutcome.Fail(
                    ErrorCodes.NothingToUndo,
                    "There is nothing to undo.",
                    null,
                    Materialise(open));
            }
        }
        catch (Exception)
        {
            // The engine put the applied state back before rethrowing, so the vote stands, the pair
            // has not moved and the token the client holds is still current: the identical undo may
            // be retried.
            return SessionOutcome.Fail(
                ErrorCodes.SaveFailed,
                "The ranking file could not be written; the action was not taken back.",
                new { recordsChanged = false, fileMoved = false },
                Materialise(open));
        }

        // One level (§ 10.10): an older move is no longer offered once this cancel has been spent.
        // Leaving it would make cancel walk backwards through the session one press at a time,
        // which is an undo stack by the back door.
        open.Actions.ClearLastMove();

        open.PairSeq++;
        open.LastSavedAt = DateTimeOffset.UtcNow;
        open.LastAction = NewAction(
            open, ActionTypes.Undo, pairToken: null, clientRequestId, undoneType: undoneType);
        return SessionOutcome.Ok(Materialise(open));
    }

    private static SnapshotLastAction NewAction(
        OpenSession open,
        string type,
        string? pairToken,
        string? clientRequestId,
        string? winner = null,
        string? side = null,
        string? id = null,
        string? restoredId = null,
        string? undoneType = null) =>
        new(
            open.PairSeq,
            type,
            pairToken,
            clientRequestId,
            winner,
            side,
            id,
            restoredId,
            undoneType,
            Rfc3339(DateTimeOffset.UtcNow));

    private static SessionOutcome NoSession() =>
        SessionOutcome.Fail(ErrorCodes.NoSession, "No session is open.");

    private SessionOutcome BodyFail(BodyError error, OpenSession open) =>
        SessionOutcome.Fail(error.Code, error.Message, error.Details, Materialise(open));

    private async Task<SessionOutcome> WithLockAsync(CancellationToken cancellation, Func<SessionOutcome> work)
    {
        var acquired = await _gate.WaitAsync(LockTimeout, cancellation);
        if (!acquired)
        {
            // § 7.3. No snapshot goes with this one: materialising one means touching
            // RankingSession, and the whole point of the failure is that we do not hold the lock.
            return SessionOutcome.Fail(
                ErrorCodes.SessionBusy,
                "The session is busy; another action is still running.",
                new { retryAfterSeconds = 1 });
        }

        try
        {
            return work();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>§ 10.1 step 1 and § 15: non-empty, rooted, no NUL, 4096 characters at most.</summary>
    private static BodyError? ResolveFolder(string raw, out string folder)
    {
        folder = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096 || raw.Contains('\0'))
            return new BodyError(ErrorCodes.InvalidPath, "'folder' must be an absolute path.", new { field = "folder" });

        if (!Path.IsPathRooted(raw))
            return new BodyError(ErrorCodes.InvalidPath, "'folder' must be an absolute path.", new { field = "folder" });

        try
        {
            // TrimEndingDirectorySeparator leaves a root alone, so "C:\" and "/" survive intact.
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        }
        catch (Exception)
        {
            return new BodyError(ErrorCodes.InvalidPath, "'folder' is not a usable path.", new { field = "folder" });
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------
    // The snapshot — § 9. Materialised inside the lock, always in full; there is no small variant.
    // ---------------------------------------------------------------------------------------

    private static string? CurrentToken(OpenSession open) =>
        open.Session.Current is { } pair
            ? PairTokens.Generate(open.Secret, open.SessionId, open.PairSeq, pair.Left.Filename, pair.Right.Filename)
            : null;

    private SessionSnapshot Materialise(OpenSession open)
    {
        var session = open.Session;
        var records = session.Records;
        var policy = MediaExtensions.RankPolicy(records.Select(r => r.Kind));
        var progress = Math.Round(LibraryProgress.Of(session.Rankable), 6, MidpointRounding.AwayFromZero);

        var counts = new SnapshotCounts(
            records.Count,
            session.Rankable.Count,
            session.UnrankedCount,
            records.Count(r => r.Kind == MediaKind.Still),
            records.Count(r => r.Kind == MediaKind.Video));

        // § 9.1: is there an action to cancel? A vote or skip leaves one in the engine; a move
        // leaves one in LibraryActions. The engine's is cleared by any structural change to the
        // record set, so "the engine has one" already means "a vote/skip was the most recent thing
        // that happened" (§ 10.10).
        var undoAvailable = session.CanUndoLastAction
            || (open.Actions.LastMove is { } move && string.Equals(move.Folder, open.Folder, PathComparison));

        return new SessionSnapshot(
            open.SessionId,
            session.Current is null ? SessionStates.Exhausted : SessionStates.Ranking,
            open.Folder,
            session.FolderName,
            policy == MediaKind.Video ? "video" : "still",
            Rfc3339(open.OpenedAt),
            open.PrefetchPairs,
            session.SessionVotes,
            counts,
            progress,
            (int)Math.Round(progress * 100, MidpointRounding.AwayFromZero),
            session.RecentCues.Select(c => c == MatchCue.Upset ? "upset" : "confirmation").ToList(),
            session.Current is { } current ? PairOf(open, current) : null,
            CurrentToken(open),
            open.PairSeq,
            session.WarmPairs.Select(p => PairOf(open, p)).ToList(),
            undoAvailable,
            open.LastAction,
            open.LastSavedAt is { } saved ? Rfc3339(saved) : null);
    }

    private SnapshotPair PairOf(OpenSession open, Pair pair) =>
        new(MediaRefOf(open, pair.Left), MediaRefOf(open, pair.Right));

    private SnapshotMediaRef MediaRefOf(OpenSession open, MediaId id)
    {
        var kind = open.Session.TryFind(id, out var record)
            ? record.Kind
            : MediaExtensions.KindOf(id.Filename) ?? MediaKind.Still;

        var (size, version) = MediaFingerprint.Of(open.Folder, id.Filename);
        var encoded = Uri.EscapeDataString(id.Filename);
        var query = version is null ? "" : "?v=" + version;
        var isStill = kind == MediaKind.Still;

        var links = new SnapshotMediaLinks(
            $"{ApiBase}/media/{encoded}/meta",
            isStill ? $"{ApiBase}/media/{encoded}/still{query}" : null,
            isStill ? $"{ApiBase}/media/{encoded}/thumb{query}" : null,
            isStill ? null : $"{ApiBase}/media/{encoded}/video{query}");

        return new SnapshotMediaRef(id.Filename, isStill ? "still" : "video", size, version, links);
    }

    /// <summary>§ 2: RFC 3339 UTC with a Z and milliseconds.</summary>
    private static string Rfc3339(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class OpenSession(
        string sessionId,
        byte[] secret,
        string folder,
        DateTimeOffset openedAt,
        RankingSession session,
        FolderLock folderLock,
        int prefetchPairs)
    {
        public string SessionId { get; } = sessionId;

        public byte[] Secret { get; } = secret;

        public string Folder { get; } = folder;

        public DateTimeOffset OpenedAt { get; } = openedAt;

        public RankingSession Session { get; } = session;

        public FolderLock Lock { get; } = folderLock;

        public int PrefetchPairs { get; } = prefetchPairs;

        public LibraryActions Actions { get; set; } = null!;

        public ulong PairSeq { get; set; }

        public SnapshotLastAction? LastAction { get; set; }

        public DateTimeOffset? LastSavedAt { get; set; }

        /// <summary>
        /// § 3.6: while a rename runs, every other mutating <c>/session*</c> call answers
        /// <c>409 rename_in_progress</c>. Reads, and the rename's own poll, are unaffected.
        /// </summary>
        public bool RenameInProgress { get; set; }
    }
}
