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
public sealed class SessionRegistry : IDisposable, Security.ISessionStatusProvider
{
    public const int DefaultPrefetchPairs = 2;
    public const string ApiBase = "/api/v1";

    /// <summary>SERVER_SPEC.md § 2.4 / § 13.1: <c>RankMaster2:SaveDelaySeconds</c>, default 2.</summary>
    public const int DefaultSaveDelaySeconds = 2;

    /// <summary>SERVER_SPEC.md § 2.4 / § 13.1: <c>RankMaster2:MaxUnsavedChoices</c>, default 5.</summary>
    public const int DefaultMaxUnsavedChoices = 5;

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

    // The bounded write-behind (SERVER_SPEC.md § 13.1). Not readonly: a host binds them from
    // configuration through ApplyDurabilityOptions before it serves anything.
    private TimeSpan _saveDelay = TimeSpan.FromSeconds(DefaultSaveDelaySeconds);
    private int _maxUnsavedChoices = DefaultMaxUnsavedChoices;

    private readonly object _flushSync = new();
    private CancellationTokenSource? _flushStop;
    private Task? _flushLoop;

    /// <param name="saveDelay">
    /// SERVER_SPEC.md § 13.1: how long a choice may sit in memory before the database is written.
    /// <see cref="TimeSpan.Zero"/> restores the old behaviour exactly — every choice saved before
    /// its response. Null means the § 2.4 default of two seconds. It is a <c>TimeSpan</c> here and
    /// whole seconds in <c>appsettings.json</c>: the contract's knob is seconds, and a test that has
    /// to watch a deferred write land should not have to wait one.
    /// </param>
    /// <param name="maxUnsavedChoices">
    /// SERVER_SPEC.md § 13.1: how many choices may be unsaved before a write is forced inside the
    /// request, whichever limit is reached first. Clamped to at least 1.
    /// </param>
    public SessionRegistry(
        ICatalog? catalog = null,
        IRatingEngine? engine = null,
        IPairSelector? selector = null,
        int prefetchPairs = DefaultPrefetchPairs,
        string? serverVersion = null,
        IRenameJournalWriter? journalWriter = null,
        TimeSpan? saveDelay = null,
        int maxUnsavedChoices = DefaultMaxUnsavedChoices)
    {
        _catalog = catalog ?? new JsonCatalog();
        _engine = engine ?? new TrueSkill();
        _selector = selector ?? new PairSelector();
        _prefetchPairs = prefetchPairs;
        _serverVersion = serverVersion
            ?? typeof(SessionRegistry).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        _journalWriter = journalWriter ?? new DefaultRenameJournalWriter();
        var delay = saveDelay ?? TimeSpan.FromSeconds(DefaultSaveDelaySeconds);
        _saveDelay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        _maxUnsavedChoices = Math.Max(1, maxUnsavedChoices);
    }

    /// <summary>
    /// SERVER_SPEC.md § 2.4: <c>RankMaster2:SaveDelaySeconds</c> and
    /// <c>RankMaster2:MaxUnsavedChoices</c>, bound from configuration by the host. Applied before
    /// the first session opens, because the choice of write-behind or save-on-choice is made once,
    /// when a <see cref="RankingSession"/> is constructed (§ 13.1, "Where it lives").
    /// <para/>
    /// A negative or zero delay is <see cref="TimeSpan.Zero"/>: save on every choice, no timer.
    /// </summary>
    public void ApplyDurabilityOptions(int saveDelaySeconds, int maxUnsavedChoices)
    {
        _saveDelay = saveDelaySeconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(saveDelaySeconds);
        _maxUnsavedChoices = Math.Max(1, maxUnsavedChoices);
        if (!DefersChoices)
            StopFlushLoop();
    }

    /// <summary>
    /// True when a vote, a skip or the cancel of one is answered before its write
    /// (SERVER_SPEC.md § 13.1). False is the old behaviour, exactly: <c>SaveDelaySeconds = 0</c>.
    /// </summary>
    private bool DefersChoices => _saveDelay > TimeSpan.Zero;

    /// <summary>
    /// The process-wide session. Exactly one exists by contract (§ 1.1), so the media layer can
    /// read this rather than plumbing the instance through every call site. A test may construct
    /// its own registry and register it in DI instead; <c>MapSessionEndpoints</c> prefers DI.
    /// </summary>
    public static SessionRegistry Shared { get; } = new();

    /// <summary>The open folder, or null. A cheap read; not a substitute for a snapshot.</summary>
    public string? OpenFolder => Volatile.Read(ref _open)?.Folder;

    /// <summary>
    /// SERVER_SPEC.md § 14's <c>session</c> block, answered without taking the session gate so that
    /// <c>/ping</c> can never queue behind a vote that is mid-save. All four fields come from one
    /// read of the same <see cref="OpenSession"/> reference, so they describe one moment; <c>state</c>
    /// is derived from <c>Current</c>, which is the same rule <see cref="Materialise"/> applies.
    /// <para/>
    /// This replaces the reflection bridge that used to stand in for it and could only ever report
    /// <c>folder</c> (A4, C15): <c>sessionId</c> and <c>state</c> were null on every ping.
    /// </summary>
    public Security.SessionStatus Current
    {
        get
        {
            var open = Volatile.Read(ref _open);
            return open is null
                ? Security.SessionStatus.Closed
                : new Security.SessionStatus(
                    true,
                    open.SessionId,
                    open.Folder,
                    open.Session.Current is null ? SessionStates.Exhausted : SessionStates.Ranking);
        }
    }

    private RankMaster2.Server.Media.MediaSessionView? _mediaView;
    private OpenSession? _mediaViewOf;
    private ulong _mediaViewSeq;

    /// <summary>
    /// The read-only window the media layer needs (<see cref="Media.IMediaSessionAccessor"/>).
    /// <para/>
    /// Media requests are GETs and SERVER_SPEC.md § 11.3 forbids them from mutating session state,
    /// so they must not queue behind a vote either. <see cref="RankingSession.Records"/> hands back
    /// its live backing list, which a vote can replace under the copy. So: read optimistically, then
    /// confirm nothing moved underneath, and cache the result against <c>PairSeq</c> — which every
    /// mutation bumps — so the common case costs a reference comparison rather than rebuilding a
    /// dictionary per image.
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

                // RankingSession replaces its record list by assigning a complete new one, never by
                // clearing and refilling the old, so this copy can never see a half-built list and
                // there is nothing here to catch (A8). What it can see is a list that was current a
                // moment ago, which is what the pairSeq re-check below is for.
                var built = BuildMediaView(open);

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

            // Step 4. § 5.3.1: details.holder is null, always. The lock is FileShare.None, which is
            // the point of it — and that same exclusivity is why no other process can open the file
            // to read who holds it. The spec says in so many words not to attempt a best-effort read
            // here; it cannot succeed and would only add a misleading error path (C6).
            var folderLock = FolderLock.TryAcquire(folder, _serverVersion);
            if (folderLock is null)
            {
                return SessionOutcome.Fail(
                    ErrorCodes.FolderLocked,
                    "That folder is in use by another Rank Master process.",
                    new { holder = (object?)null });
            }

            // A9: a crash between JsonCatalog.Save's write and its File.Replace leaves
            // rankmaster_db.json.tmp beside the photos. The scan skips it and the next save
            // overwrites it, so it costs nothing but the owner's puzzlement at a stray file in his
            // pictures folder. Open is the one moment we know no save is in flight here.
            RemoveStaleDatabaseTemp(folder);

            // Step 4.5 (§ 10.16): recovery runs before anything scans. If a rename was interrupted,
            // <folder>/.rankmaster-rename.json is still here; reunite every rating with its file at
            // that file's current name before RankingSession.Start() ever calls JsonCatalog.Scan —
            // the plain Scan/Save merge is exactly the thing that would silently drop them (§ 1).
            if (RenameEngine.JournalExists(folder))
            {
                RenameRecoveryOutcome recovery;
                try
                {
                    recovery = RenameEngine.RecoverIfPresent(folder, _catalog);
                }
                catch (Exception)
                {
                    // § 10.16, "An unreadable journal is refused, not guessed at": a journal that
                    // cannot be read (bad format, null plan, truncated or hand-edited JSON, a plan
                    // whose old and new sets intersect) makes ReadJournal throw InvalidDataException,
                    // and a disk fault could throw anything else. Either way nothing has been moved
                    // and nothing written — but the lock was taken two steps ago, and letting the
                    // exception leave here is how a folder ends up answering 423 folder_locked ("in
                    // use by another Rank Master process") to the owner himself until the server is
                    // restarted (H4). Release it, then answer the truth.
                    folderLock.Dispose();
                    return SessionOutcome.Fail(
                        ErrorCodes.RenameFailed,
                        "An interrupted rename left a journal this server cannot read; nothing was changed.",
                        new { reunited = false, journal = RenameEngine.JournalPath(folder) });
                }

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
            // § 13.1, "Where it lives": with the write-behind on, the engine stops calling Save on a
            // choice and this registry owns when the write happens and what a failure means. With
            // SaveDelaySeconds = 0 it is constructed exactly as the desktop app constructs it.
            var ranking = new RankingSession(
                folder, _catalog, _engine, _selector, _prefetchPairs, saveOnChoice: !DefersChoices);
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
                _prefetchPairs,
                new LibraryActions(
                    () => folder,
                    _catalog,
                    () => _pipeline,
                    () => ranking,
                    // LibraryActions was written for the desktop shell, which had to let a decoder
                    // go before moving the file under it. Nothing at this layer decodes anything:
                    // media streams are opened FileShare.Delete, so a move needs no warning (C13).
                    _ => { }));

            _open = opened;
            EnsureFlushLoop();

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

            // § 10.4 and § 10.16: refused while a rename runs, like every other mutating call. It is
            // the one that looks harmless and is not — closing releases the folder lock while the
            // rename task is still moving files, which lets a second POST /session start journal
            // recovery on a folder the first session is halfway through renaming, two programs
            // finalizing the same plan at once. The client cancels the rename first (H14).
            if (open.RenameInProgress)
                return RenameInProgress(open);

            // § 10.4, § 13.1: the close is a durability point — whatever the write-behind is still
            // holding goes to disk before the lock is released, so no choice the owner made in this
            // session is lost by closing. A flush that fails does not refuse the close: a close a
            // broken disk could block would be a wedge, the choices were already reported applied,
            // and there is nothing the client could usefully do with the refusal.
            FlushUnderGate(open);

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
                // § 13.1: if there were unsaved choices, this failure is the latch — the next
                // mutating call retries the write and answers save_failed with nothing applied
                // rather than letting more choices pile up on a disk that is not taking them.
                if (open.UnsavedChoices > 0)
                    open.LatchSaveFailure(DateTimeOffset.UtcNow);

                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The ranking file could not be written.",
                    new { recordsChanged = false, fileMoved = false },
                    Materialise(open));
            }

            // § 10.5: "this is the way a client makes a point durable", and § 13.1: a successful
            // save clears the latched failure, which makes this the recovery path after the disk
            // came back.
            open.MarkSaved(DateTimeOffset.UtcNow);
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

            // § 13.1: a latched write failure is answered before anything is applied, so the second
            // choice after the drive went away tells the owner instead of vanishing with the rest.
            var latched = RefuseWhileSaveIsLatched(open);
            if (latched is not null)
                return latched;

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
            AfterChoice(open);
            open.UndoPoint = UndoPoint.EngineSnapshot;
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

            var latched = RefuseWhileSaveIsLatched(open);
            if (latched is not null)
                return latched;

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
            AfterChoice(open);
            open.UndoPoint = UndoPoint.EngineSnapshot;
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

            // § 13.1, "What is never deferred", and § 13.2: anything the write-behind is still
            // holding is written *before* the file moves. That is what keeps § 13.2's ordering —
            // file moved, then JSON written — and with it the self-heal: if the process dies between
            // the move and the save, the only row the next Scan/Save drops is the file that really
            // did move away. A flush that fails refuses the move outright, with nothing applied and
            // the token still current, because moving a file on a disk that will not take the
            // database is how a rating gets separated from its picture.
            var flushed = FlushOrRefuse(open);
            if (flushed is not null)
                return flushed;

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

                // § 10.10: drop_missing is not cancellable, and it does not hand the cancel on to
                // whatever came before it. Drop already cleared the engine's snapshot; clearing the
                // recorded move too is what stops a discard from three actions ago being restored by
                // an undo the client was told was available (H5, A7).
                open.Actions.ClearLastMove();
                open.UndoPoint = UndoPoint.None;

                try
                {
                    open.Session.Save();
                }
                catch (Exception)
                {
                    // The record is gone from memory and the database does not know: that is an
                    // unsaved change, and § 13.1 latches it so the next call retries the write and
                    // says so rather than stacking more on top of it.
                    open.RecordUnsavedChoice(DateTimeOffset.UtcNow);
                    open.LatchSaveFailure(DateTimeOffset.UtcNow);
                    open.LastAction = NewAction(
                        open, ActionTypes.DropMissing, pairToken: null, clientRequestId, id: id.Filename);
                    return SessionOutcome.Fail(
                        ErrorCodes.SaveFailed,
                        "The record was dropped but the ranking file could not be written.",
                        new { recordsChanged = true, fileMoved = false },
                        Materialise(open));
                }

                open.MarkSaved(DateTimeOffset.UtcNow);
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
                // The file is in discarded/ or special 1/ and the database did not follow: undo is
                // the only way back, so it must be offered (§ 8.3). The database is now behind the
                // truth, so the same latch applies (§ 13.1) — the next mutating call retries the
                // write first.
                open.RecordUnsavedChoice(DateTimeOffset.UtcNow);
                open.LatchSaveFailure(DateTimeOffset.UtcNow);
                open.UndoPoint = UndoPoint.LastMove;
                open.LastAction = NewAction(open, actionType, token, clientRequestId, side: side, id: id.Filename);
                return SessionOutcome.Fail(
                    ErrorCodes.SaveFailed,
                    "The file was moved but the ranking file could not be written.",
                    new { recordsChanged = true, fileMoved = true },
                    Materialise(open));
            }

            open.PairSeq++;
            // LibraryActions.Move saved after the move (§ 13.2), so the session is clean again.
            open.MarkSaved(DateTimeOffset.UtcNow);
            open.UndoPoint = UndoPoint.LastMove;
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

            // § 10.10, one level, one source of truth. The session records what the last cancellable
            // thing was — a vote/skip (the engine holds the snapshot) or a move (LibraryActions holds
            // the file) — and every structural change clears it. Asking the engine and LibraryActions
            // separately, and OR-ing the two answers, is what let an undo reach past a drop_missing
            // to a discard the owner had made on purpose (H5, A7).
            if (open.UndoPoint == UndoPoint.EngineSnapshot)
            {
                // Cancelling a vote or a skip moves no file: it is a plain choice, deferred and
                // bounded exactly like the one it reverses (§ 13.2's undo row).
                var latched = RefuseWhileSaveIsLatched(open);
                return latched ?? UndoAction(open, clientRequestId);
            }

            if (open.UndoPoint != UndoPoint.LastMove || open.Actions.LastMove is not { } move)
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
                open.UndoPoint = UndoPoint.None;
                return SessionOutcome.Fail(
                    ErrorCodes.UndoFolderChanged,
                    "The recorded move belongs to a different folder.",
                    new { moveFolder = move.Folder },
                    Materialise(open));
            }

            // The undo of a move is a move (§ 13.1, § 13.2): flush first, in the same order and for
            // the same reason as the discard it is taking back.
            var flushed = FlushOrRefuse(open);
            if (flushed is not null)
                return flushed;

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
                open.UndoPoint = UndoPoint.None;
                open.PairSeq++;
                // The file is back and the database does not know it: unsaved, and latched (§ 13.1).
                open.RecordUnsavedChoice(DateTimeOffset.UtcNow);
                open.LatchSaveFailure(DateTimeOffset.UtcNow);
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
            // LibraryActions.UndoLastMove saved after the move back, so the session is clean again.
            open.MarkSaved(DateTimeOffset.UtcNow);
            open.UndoPoint = UndoPoint.None;
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

    public async Task<RenameOutcome> StartRenameAsync(
        string? clientRequestId, CancellationToken cancellation)
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

            // § 13.1, "What is never deferred": before a rename starts, everything unsaved goes to
            // disk. A rename rewrites the whole database from the records it captures here, so this
            // is not what keeps the ratings safe — what it keeps is the promise that a disk which
            // cannot take a write does not get a folder full of files moved on it first. The refusal
            // is `rename_failed { reunited: true, journal: null }`, the same answer § 10.16 gives
            // when the journal write throws: nothing was disturbed, no journal exists, and the
            // session is left exactly as it was. (§ 5.7 gives the rename endpoints one 500 code;
            // save_failed is not one of them.)
            if (!FlushUnderGate(open))
            {
                return RenameOutcome.Fail(
                    ErrorCodes.RenameFailed,
                    "Choices are still unwritten and the ranking file could not be written; nothing was changed.",
                    new { reunited = true, journal = (string?)null },
                    Materialise(open));
            }

            // § 3.3 "preparing": order by μ − 3σ desc, then filename, from the records as they are
            // right now, and draw the run suffix against both those names and everything the folder
            // currently lists — including files this session never knew about (§ 10.16, "The
            // names"). Sixteen rejected draws is `rename_failed`, never `internal_error`, and
            // nothing has moved.
            List<PlanEntry> plan;
            try
            {
                plan = RenameEngine.BuildPlan(open.Folder, open.Session.Records);
            }
            catch (Exception)
            {
                return RenameOutcome.Fail(
                    ErrorCodes.RenameFailed,
                    "A rename plan could not be built for this folder; nothing was changed.",
                    new { reunited = true, journal = (string?)null },
                    Materialise(open));
            }

            run = new RenameRun(PairTokens.NewSessionId(), open.Folder, open.Session.Records, DateTimeOffset.UtcNow)
            {
                Plan = plan,
                ClientRequestId = clientRequestId,
            };

            // § 10.16, "The flag is set after the journal write, never before." The run is published
            // to _rename first so a cancel racing a slow (or test-blocked) journal write can still
            // flag it — that costs nothing, because a published run with the flag unset refuses
            // nothing. The flag is what makes every other call answer 409 rename_in_progress, and
            // setting it before a write that can throw is what left sessions wedged behind "A rename
            // is already running" with no run to finish it and no cancel to clear it (H3).
            Volatile.Write(ref _rename, run);

            try
            {
                _journalWriter.Write(open.Folder, plan, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                // A read-only folder or a full disk. Nothing was disturbed and no rating is in
                // doubt, so reunited is true; no journal was written, so journal is null.
                Volatile.Write(ref _rename, null);
                RenameEngine.DeleteJournal(open.Folder);
                return RenameOutcome.Fail(
                    ErrorCodes.RenameFailed,
                    "The rename journal could not be written; the session is untouched.",
                    new { reunited = true, journal = (string?)null },
                    Materialise(open));
            }

            open.RenameInProgress = true;

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
                // § 10.16: total is N, the number of files in the plan. One move per file, so `done`
                // reaches `total` exactly once.
                run.MarkPreparingDone(total: plan.Count);
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
    /// The live forward operation (§ 3.3), off the session lock: one move phase, the commit, the
    /// journal delete (the commit point), then the apply back under the lock. Cancellation is polled
    /// before every move and between the retries of a move waiting on a locked file; once the commit
    /// has begun it is too late (§ 3.5) and the run always finishes to a terminus.
    /// </summary>
    private async Task RunRenameAsync(OpenSession open, RenameRun run)
    {
        var plan = run.Plan;

        try
        {
            // One move per file, old → new. There is no second phase: the run suffix has already
            // proven the old and new name sets are disjoint, so there is no cycle to break and
            // nothing a temporary name would be for (§ 10.16, "One move per file").
            RenameEngine.MoveAll(
                open.Folder, plan,
                shouldStop: _ => run.CancelRequested,
                onProgress: (done, _) => run.ReportProgress(RenamePhases.Renaming, done));

            if (run.CancelRequested)
            {
                await CancelInPlaceAsync(open, run);
                return;
            }

            run.ReportProgress(RenamePhases.Saving, plan.Count);

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
                await FailAndResyncAsync(open, run, recovery.Reunited, recovery.JournalPath);
                return;
            }

            RenameEngine.DeleteJournal(open.Folder);   // the commit point (§ 3.3 step 4)

            await FinishAsync(
                open,
                applyToSession: () =>
                {
                    open.Session.ReplaceAll(remapped);
                    open.Actions.ClearLastMove();
                    open.UndoPoint = UndoPoint.None;
                    open.PairSeq++;
                    open.MarkSaved(DateTimeOffset.UtcNow);
                    open.LastAction = new SnapshotLastAction(
                        open.PairSeq, ActionTypes.Rename,
                        PairToken: null, ClientRequestId: run.ClientRequestId, Winner: null, Side: null,
                        Id: null, RestoredId: null, UndoneType: null, At: Rfc3339(DateTimeOffset.UtcNow));
                    open.RenameInProgress = false;
                },
                markRun: run.Succeed);
        }
        catch (OperationCanceledException)
        {
            // FileOps.MoveWithRetry throws this when the cancel lands between the retries of a move
            // that is waiting on a file another program holds — the owner's slow-USB case. It is the
            // cancel path, not a failure: the button he pressed did exactly what it says.
            await CancelInPlaceAsync(open, run);
        }
        catch (Exception)
        {
            // Anything else that went wrong mid-move: the journal is still on disk (it is only
            // deleted at the commit point above), so recovery from it is exactly the right answer,
            // identical to what the next POST /session would do if the process had died here.
            var recovery = RenameEngine.RecoverIfPresent(open.Folder, _catalog);
            await FailAndResyncAsync(open, run, recovery.Reunited, recovery.JournalPath);
        }
    }

    /// <summary>§ 3.5: stop issuing moves, reunite in place (never finalizing the rest), then resync
    /// the session — filenames on disk are now a mix of old and new, and what the session holds in
    /// memory is neither.</summary>
    private async Task CancelInPlaceAsync(OpenSession open, RenameRun run)
    {
        run.ReportPhase(RenamePhases.Reuniting);

        RenameRecoveryOutcome outcome;
        try
        {
            outcome = RenameEngine.ReuniteInPlace(open.Folder, _catalog);
        }
        catch (Exception)
        {
            outcome = new RenameRecoveryOutcome(true, false, RenameEngine.JournalPath(open.Folder));
        }

        if (!outcome.Reunited)
        {
            await FailAndResyncAsync(open, run, outcome.Reunited, outcome.JournalPath);
            return;
        }

        // Starts true: if there is no session left to resync (it went while the run was off the
        // lock), the ratings were still reunited and `cancelled` is still the honest terminus.
        var resynced = true;
        await FinishAsync(
            open,
            applyToSession: () => resynced = ResyncAfterRename(open),
            markRun: () =>
            {
                // The ratings were reunited either way; `cancelled` is the honest terminus unless
                // the folder itself could not be re-read, and then the session is already closed.
                if (resynced)
                    run.Cancel();
                else
                    run.Fail(reunited: true, journal: null);
            });
    }

    /// <summary>
    /// The failure terminus, with the same session effect a cancel has (§ 7.2, § 10.16 "Cancel or
    /// failure — the session effect"). The folder is in the same condition either way — some files
    /// moved, some did not, the ratings reunited in place — so the session is resynced identically.
    /// Only <c>reunited</c> differs, and it states the truth about the ratings (H2, A1, C1, C11).
    /// </summary>
    private Task FailAndResyncAsync(OpenSession open, RenameRun run, bool reunited, string? journal) =>
        FinishAsync(
            open,
            applyToSession: () => ResyncAfterRename(open),
            markRun: () => run.Fail(reunited, journal));

    /// <summary>
    /// Re-reads the folder into the open session and says so honestly. Called under the gate, from
    /// both terminal paths that leave the folder half-renamed.
    ///
    /// <para><c>sessionId</c> unchanged — it is the same session. <c>sessionVotes</c> and the cue
    /// strip kept — the owner cast those votes and a rename that did not finish is no reason to
    /// forget them (this is why it is <see cref="RankingSession.Resync"/> and not <c>Start()</c>,
    /// which zeroes exactly the counters § 10.1 goes out of its way to protect on a resume).
    /// <c>pairSeq</c> +1 and undo cleared — the generation genuinely changed and the undo point
    /// refers to files that may have moved, so every token a client holds is stale and the snapshot
    /// that comes back says so. <c>lastAction</c> null.</para>
    ///
    /// <para>Returns false when the folder itself could not be re-read — unmounted, deleted, made
    /// unreadable during the run — in which case the session is closed and the lock released:
    /// "A session whose folder cannot be read is not a session" (§ 10.16).</para>
    /// </summary>
    private bool ResyncAfterRename(OpenSession open)
    {
        try
        {
            open.Session.Resync();
        }
        catch (Exception)
        {
            open.RenameInProgress = false;
            if (ReferenceEquals(_open, open))
            {
                _open = null;
                Volatile.Write(ref _rename, null);
            }

            open.Lock.Dispose();
            return false;
        }

        open.Actions.ClearLastMove();
        open.UndoPoint = UndoPoint.None;
        open.PairSeq++;
        open.LastAction = null;
        open.RenameInProgress = false;
        return true;
    }

    /// <summary>
    /// Re-acquires the session gate to apply the run's result. § 3.4: the run is server-owned and
    /// reaches a consistent terminus even if the client vanished — including if the session itself
    /// was closed and possibly reopened while the run was off the lock, in which case there is
    /// nothing left in memory to apply to; the filesystem and database work already committed
    /// correctly regardless, so this is not a failure, just nothing further to do.
    ///
    /// <para>The wait is unbounded. The five-second cap is for an HTTP caller, who has a client
    /// waiting on the other end and must be told <c>503 session_busy</c> rather than left hanging;
    /// this is the server finishing its own work, and giving up on it would leave
    /// <c>RenameInProgress</c> set for ever, with the operation stuck in <c>saving</c> and no cancel
    /// able to clear it — a wedge only a restart cures (H3's second half).</para>
    /// </summary>
    private async Task FinishAsync(OpenSession open, Action applyToSession, Action markRun)
    {
        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_open, open))
                applyToSession();
            else
                open.RenameInProgress = false;

            // The operation reaches its terminus either way, so a client that is still polling is
            // never left watching a run that can no longer move.
            markRun();
        }
        finally
        {
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

    /// <summary>
    /// Shutdown (A3). <c>Rm2Host</c> registers this on <c>ApplicationStopping</c>, and closing the
    /// open session is the whole point: the OS releases the handle when the process dies either way,
    /// but <c>&lt;folder&gt;/.rankmaster.lock</c> is deleted by <see cref="FolderLock.Dispose"/> and
    /// by nothing else — so without this the owner is left with a stray file sitting next to his
    /// photographs after every clean exit.
    ///
    /// <para>The semaphore is deliberately not disposed. This type has a process-wide
    /// <see cref="Shared"/> instance that more than one host can reach, and a disposed semaphore
    /// turns a later request into an <c>ObjectDisposedException</c> rather than an honest answer;
    /// the handle it would release costs nothing at exit. Idempotent.</para>
    /// </summary>
    public void Dispose()
    {
        // Stopped first, and outside the _disposed guard: a host that was rebuilt after an earlier
        // Dispose has a live timer again (EnsureFlushLoop runs on every open), and leaving it
        // ticking against a registry nobody serves from is a background task with no owner.
        StopFlushLoop();

        if (_disposed)
            return;
        _disposed = true;

        var open = _open;

        // § 13.1: a clean shutdown is a durability point, so whatever the write-behind is holding
        // goes to disk here. The gate is taken with the ordinary timeout rather than waited on for
        // ever: if a request is wedged holding it while the process is being stopped, hanging the
        // shutdown to save five choices is the worse trade, and losing them is exactly the bound the
        // owner accepted.
        if (open is not null && open.UnsavedChoices > 0 && _gate.Wait(LockTimeout))
        {
            try
            {
                FlushUnderGate(open);
            }
            finally
            {
                _gate.Release();
            }
        }

        _open = null;
        Volatile.Write(ref _rename, null);
        open?.Lock.Dispose();
    }

    // ---------------------------------------------------------------------------------------
    // The bounded write-behind — SERVER_SPEC.md § 13.1.
    //
    // A choice (vote, skip, or the cancel of one) is applied in memory and answered at once; the
    // database is written off the request path, no later than the earlier of SaveDelaySeconds after
    // the first unsaved choice or the MaxUnsavedChoices-th unsaved choice. Everything that is not a
    // plain choice — a file move, the start of a rename, POST /session/save, DELETE /session,
    // shutdown — forces the write synchronously first, so § 13.2's ordering and the discard
    // self-heal argument are unchanged.
    //
    // Nothing here changes what a write *is*. JsonCatalog.Save is untouched: temp file, Flush(true),
    // File.Replace, never creating the folder, never writing an empty database over records. Five
    // choices coalesce into one write of the same file, which is the whole of the gain.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Started when a session opens and cancelled at <see cref="Dispose"/>, so a registry that is
    /// never used costs nothing and a host that is rebuilt (the test suites do this) gets a live
    /// timer again on its next open.
    /// </summary>
    private void EnsureFlushLoop()
    {
        if (!DefersChoices)
            return;

        lock (_flushSync)
        {
            if (_flushLoop is { IsCompleted: false })
                return;

            _flushStop?.Dispose();
            _flushStop = new CancellationTokenSource();
            var stop = _flushStop.Token;
            _flushLoop = Task.Run(() => FlushLoopAsync(stop), CancellationToken.None);
        }
    }

    private void StopFlushLoop()
    {
        lock (_flushSync)
        {
            try
            {
                _flushStop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// The tick of the flush timer. § 13.1 bounds the wait at <c>SaveDelaySeconds</c> <i>after the
    /// first unsaved choice</i>, so the timer has to be finer than the bound it enforces: a period
    /// equal to the bound would let a choice made just after a tick wait almost twice as long.
    /// A quarter of the delay, never below 25 ms and never above the delay itself.
    /// </summary>
    private TimeSpan FlushTick()
    {
        var tick = TimeSpan.FromTicks(Math.Max(_saveDelay.Ticks / 4, TimeSpan.TicksPerMillisecond * 25));
        return tick > _saveDelay ? _saveDelay : tick;
    }

    private async Task FlushLoopAsync(CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(FlushTick());
            while (await timer.WaitForNextTickAsync(stop))
            {
                // Read without the gate first: the overwhelmingly common tick has nothing to do, and
                // queueing behind a vote to discover that would make the timer itself a source of
                // latency. FlushUnderGate re-reads _open inside the gate.
                var pending = Volatile.Read(ref _open);
                if (pending is null || !pending.FlushDue(_saveDelay, DateTimeOffset.UtcNow))
                    continue;

                // § 13.1: "the flush waits for it without the 5 s client timeout" — there is no
                // client on the other end of this to answer 503 to.
                await _gate.WaitAsync(stop);
                try
                {
                    FlushUnderGate(Volatile.Read(ref _open));
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose, or the host shutting down. The shutdown flush is Dispose's own job.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Writes everything unsaved. MUST be called with the session gate held. Returns false when the
    /// write threw, having latched the failure on the session (§ 13.1, "When a deferred write
    /// fails") — the retry is then <c>SaveDelaySeconds</c> away and the next mutating call answers
    /// <c>500 save_failed</c> with nothing applied.
    /// </summary>
    private bool FlushUnderGate(OpenSession? open)
    {
        if (open is null || open.UnsavedChoices == 0)
            return true;

        // Never while a rename is running. The rename is rewriting the database from the records it
        // captured, off the gate, and the session in memory still holds the pre-rename names; a
        // flush landing in the middle would write those names back over the commit. It cannot
        // normally arise — the rename forces the write before it starts and every mutating call is
        // refused while it runs, so there is nothing unsaved — and this is the guard that keeps it
        // that way rather than by inspection.
        if (open.RenameInProgress)
            return true;

        try
        {
            open.Session.Save();
        }
        catch (Exception)
        {
            open.LatchSaveFailure(DateTimeOffset.UtcNow);
            return false;
        }

        open.MarkSaved(DateTimeOffset.UtcNow);
        return true;
    }

    /// <summary>
    /// § 13.1, "What is never deferred": before a file move, before a rename starts, and before the
    /// close, everything unsaved goes to disk first, inside the request. Null when the session is
    /// clean or the write succeeded; otherwise the <c>500 save_failed</c> with nothing applied that
    /// § 8.3's vote/skip row describes — <c>pairSeq</c> has not moved and the client's token is
    /// still current, so the identical request may be retried.
    /// </summary>
    private SessionOutcome? FlushOrRefuse(OpenSession open) =>
        FlushUnderGate(open) ? null : SaveFailedWithNothingApplied(open);

    /// <summary>
    /// § 13.1: a plain choice does not force a write, but it does refuse to pile up on a dead disk.
    /// While a failed flush is latched, every mutating call first attempts the write and, if that
    /// fails too, answers <c>save_failed</c> with nothing applied. That is what makes the second
    /// choice after the drive goes away tell the owner, rather than the fifth or the fiftieth.
    /// </summary>
    private SessionOutcome? RefuseWhileSaveIsLatched(OpenSession open) =>
        !open.SaveFailed ? null : FlushOrRefuse(open);

    private SessionOutcome SaveFailedWithNothingApplied(OpenSession open) =>
        SessionOutcome.Fail(
            ErrorCodes.SaveFailed,
            "The ranking file could not be written; nothing was applied.",
            new { recordsChanged = false, fileMoved = false },
            Materialise(open));

    /// <summary>
    /// What happens after a vote, a skip or the cancel of one has been applied, inside the gate and
    /// before the response is materialised (§ 13.1's response order).
    /// <para/>
    /// With <c>SaveDelaySeconds = 0</c> the engine has already saved and this only records the
    /// moment. Otherwise the choice is counted; the <c>MaxUnsavedChoices</c>-th is written here,
    /// synchronously, before the response leaves. A write that throws at that point is latched, not
    /// answered: the choice itself is applied, <c>pairSeq</c> has advanced and the pair on screen has
    /// moved on, so reporting "nothing applied" would be a lie — and the latch means the very next
    /// choice tells the truth instead.
    /// </summary>
    private void AfterChoice(OpenSession open)
    {
        if (!DefersChoices)
        {
            open.MarkSaved(DateTimeOffset.UtcNow);
            return;
        }

        open.RecordUnsavedChoice(DateTimeOffset.UtcNow);
        if (open.UnsavedChoices >= _maxUnsavedChoices)
            FlushUnderGate(open);
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
        open.UndoPoint = UndoPoint.None;

        open.PairSeq++;
        AfterChoice(open);
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

    /// <summary>
    /// § 10.1 step 1 and § 15, through the same <see cref="Security.PathGuard"/> that
    /// <c>GET /libraries/browse</c> uses (H11). Browsing the whole filesystem is the product
    /// decision and this does not change it: what it closes is that the endpoints which
    /// <i>move files</i> used to accept path forms the read-only endpoint refuses — <c>..</c>
    /// segments, UNC (<c>\\host\share</c>, which on Windows makes the server authenticate to a
    /// host of the caller's choosing), Win32 device namespaces, reserved device names, and segments
    /// with a trailing dot or space. One guard, one answer, one canonical path.
    /// </summary>
    private static BodyError? ResolveFolder(string raw, out string folder)
    {
        folder = "";
        var check = Security.PathGuard.Check(raw);
        if (!check.Ok)
            return new BodyError(ErrorCodes.InvalidPath, DescribeRejection(check.Reason), new { field = "folder" });

        folder = check.Canonical;
        return null;
    }

    /// <summary>
    /// Human text only. Like the browse endpoint's, it names nothing the caller did not send and
    /// never reports which check failed in a machine-readable field: <c>details</c> stays the
    /// <c>{ field }</c> shape § 5.2 fixes for <c>invalid_path</c>.
    /// </summary>
    private static string DescribeRejection(Security.PathRejection reason) => reason switch
    {
        Security.PathRejection.Empty => "'folder' is required.",
        Security.PathRejection.TooLong => "'folder' is longer than 4096 characters.",
        Security.PathRejection.Nul => "'folder' contains a NUL character.",
        Security.PathRejection.ControlCharacter => "'folder' contains a control character.",
        Security.PathRejection.NotAbsolute => "'folder' must be an absolute path.",
        Security.PathRejection.Traversal => "'folder' contains a relative segment.",
        Security.PathRejection.Unc => "UNC and device paths cannot be opened.",
        Security.PathRejection.DeviceName => "'folder' names a reserved device.",
        Security.PathRejection.TrailingDotOrSpace => "A segment of 'folder' ends with a dot or a space.",
        _ => "'folder' is not a usable path.",
    };

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

        // § 9.1: is there an action to cancel? Exactly one field answers that, the same field
        // POST /session/undo consults, so the snapshot cannot offer a cancel the undo would refuse
        // or refuse one it would take (A7).
        var undoAvailable = open.UndoPoint != UndoPoint.None;

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

        var (size, version) = FingerprintOf(open.Folder, id.Filename);
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

    /// <summary>
    /// § 12.2's <c>mediaVersion</c>, from the one implementation of the fingerprint the server has
    /// (<see cref="Media.MediaFingerprint"/>). There used to be a second, private to this folder,
    /// which agreed with it only by inspection (C16).
    ///
    /// <para>Null when the file cannot be stat'd — it was moved or deleted under the session
    /// (§ 7.4.8) — and <c>sizeBytes</c> is then null too (§ 9.3).</para>
    /// </summary>
    private static (long? SizeBytes, string? MediaVersion) FingerprintOf(string folder, string id)
    {
        try
        {
            var info = new FileInfo(Path.Combine(folder, id));
            if (!info.Exists)
                return (null, null);

            return (info.Length, Media.MediaFingerprint.Of(id, info).MediaVersion);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// A9: removes <c>rankmaster_db.json.tmp</c>, left behind by a crash between
    /// <c>JsonCatalog.Save</c>'s write and its atomic replace. It is skipped by every scan and
    /// overwritten by the next save, so the only thing it costs is the owner finding a file he did
    /// not put there among his pictures. Failure to delete is not a reason to refuse the folder.
    /// </summary>
    private static void RemoveStaleDatabaseTemp(string folder)
    {
        try
        {
            var stale = Path.Combine(folder, JsonCatalog.FileName + ".tmp");
            if (File.Exists(stale))
                File.Delete(stale);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
        int prefetchPairs,
        LibraryActions actions)
    {
        public string SessionId { get; } = sessionId;

        public byte[] Secret { get; } = secret;

        public string Folder { get; } = folder;

        public DateTimeOffset OpenedAt { get; } = openedAt;

        public RankingSession Session { get; } = session;

        public FolderLock Lock { get; } = folderLock;

        public int PrefetchPairs { get; } = prefetchPairs;

        /// <summary>
        /// Constructed with the session, not assigned afterwards: <c>null!</c> plus "someone always
        /// fills this in" is a promise the type cannot keep, and every read of it here would have
        /// had to trust it (C20).
        /// </summary>
        public LibraryActions Actions { get; } = actions;

        /// <summary>
        /// § 9.1 and § 10.10: the one thing that can be cancelled, one level, one source. Set by
        /// vote and skip (the engine's snapshot) and by discard and special (the file that moved);
        /// cleared by <c>drop_missing</c>, by a successful rename, by a resync, by the undo itself,
        /// and at open. <c>undoAvailable</c> is this field and nothing else.
        /// </summary>
        public UndoPoint UndoPoint { get; set; } = UndoPoint.None;

        public ulong PairSeq { get; set; }

        public SnapshotLastAction? LastAction { get; set; }

        public DateTimeOffset? LastSavedAt { get; set; }

        /// <summary>
        /// SERVER_SPEC.md § 13.1: how many choices have been applied in memory and not yet written.
        /// Zero means the database on disk agrees with this session. Only ever read or written under
        /// the session gate.
        /// </summary>
        public int UnsavedChoices { get; private set; }

        /// <summary>When the first of those choices was made — the clock the bound is measured on.</summary>
        public DateTimeOffset? DirtySince { get; private set; }

        /// <summary>
        /// § 13.1, "When a deferred write fails": a flush that threw is latched here. While it is
        /// set, every mutating call attempts the write first and answers <c>500 save_failed</c> with
        /// nothing applied if it fails again; a successful write of any kind clears it.
        /// </summary>
        public bool SaveFailed { get; private set; }

        public void RecordUnsavedChoice(DateTimeOffset now)
        {
            UnsavedChoices++;
            DirtySince ??= now;
        }

        /// <summary>The database is on disk and agrees with this session: the bound starts again.</summary>
        public void MarkSaved(DateTimeOffset now)
        {
            UnsavedChoices = 0;
            DirtySince = null;
            SaveFailed = false;
            LastSavedAt = now;
        }

        /// <summary>
        /// The write threw. <c>DirtySince</c> is moved to now so the flush retries one whole
        /// <c>SaveDelaySeconds</c> later rather than on every tick of a loop that is already failing.
        /// </summary>
        public void LatchSaveFailure(DateTimeOffset now)
        {
            SaveFailed = true;
            DirtySince = now;
        }

        /// <summary>§ 13.1's bound: <c>SaveDelaySeconds</c> after the first unsaved choice.</summary>
        public bool FlushDue(TimeSpan delay, DateTimeOffset now) =>
            UnsavedChoices > 0 && DirtySince is { } since && now - since >= delay;

        /// <summary>
        /// § 3.6: while a rename runs, every other mutating <c>/session*</c> call answers
        /// <c>409 rename_in_progress</c>. Reads, and the rename's own poll, are unaffected.
        /// </summary>
        public bool RenameInProgress { get; set; }
    }
}

/// <summary>
/// SERVER_SPEC.md § 9.1 / § 10.10: what a cancel would take back, if anything. One level, and
/// exactly one of these at a time — which is the point. <c>undoAvailable</c> on the wire is
/// <c>!= None</c>, and <c>POST /session/undo</c> branches on the same value, so the snapshot and the
/// endpoint can no longer disagree (A7).
/// </summary>
internal enum UndoPoint
{
    /// <summary>Nothing to cancel: a fresh session, or the cancel has been spent.</summary>
    None,

    /// <summary>A vote or a skip. The engine holds the snapshot; nothing moved on disk.</summary>
    EngineSnapshot,

    /// <summary>A discard or a special. The file is in a subfolder and undo moves it back.</summary>
    LastMove,
}
