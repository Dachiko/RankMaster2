namespace RankMaster2.Pc.Link;

using RankMaster2.Pc.Link.Wire;

/// <summary>
/// The PC client's only way to talk to the server. One instance per process.
///
/// Call it from one thread, one call at a time: a call made while another is in flight throws
/// InvalidOperationException. Nothing here runs in the background — every change to State or
/// Snapshot is the return value of a call the caller made.
///
/// No member of this interface accepts or returns a pairToken. The link holds the token of the
/// pair in Snapshot and sends it itself; a lost response is retried inside the call with the same
/// token (SERVER_SPEC.md § 13.3). This is what makes a double vote impossible to express.
/// </summary>
public interface ISessionLink : IAsyncDisposable
{
    /// <summary>Where the link stands. Changes only as the result of a call.</summary>
    LinkState State { get; }

    /// <summary>The last snapshot adopted, or null before a session is open. The pair on screen.</summary>
    Snapshot? Snapshot { get; }

    /// <summary>The failure the last call ended with, or null. For a status line; results are the primary channel.</summary>
    Failure? LastFailure { get; }

    /// <summary>True while a call is in flight. Callers ignore keys while it is true.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Find, trust and authenticate with the server: stored credential, else enrol through the
    /// server's data directory, starting the server first if it is not running (§ 5.2). Idempotent.
    /// Never opens a session.
    /// </summary>
    Task<ConnectResult> ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Open a folder: connects if needed, then DELETE /session (404 is fine) and POST /session, so
    /// the server runs Start() and picks a fresh pair. Resolves a session another client left open
    /// without asking (§ 5.3).
    /// </summary>
    Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default);

    /// <summary>
    /// Vote on the pair the caller is looking at. <paramref name="onPairSeq"/> is Snapshot.PairSeq
    /// as rendered; if it no longer matches, nothing is sent and the result is NotSent(PairMoved).
    /// It is compared locally and never transmitted (SERVER_SPEC.md § 9.1).
    /// </summary>
    Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default);
    Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default);
    Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default);
    Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default);

    /// <summary>POST /session/undo. Not pair-scoped, takes no pairSeq. NotSent(UndoNotAvailable) when Snapshot.UndoAvailable is false.</summary>
    Task<ActionResult> UndoAsync(CancellationToken ct = default);

    /// <summary>POST /session/save. Never changes the pair.</summary>
    Task<ActionResult> SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// SERVER_SPEC.md § 10.16: POST /session/rename. No token — a rename is not pair-scoped. The
    /// 202 this carries is an acknowledgement, not a completion (durability is asserted only once
    /// <see cref="GetRenameAsync"/> observes <c>succeeded</c>); the caller polls
    /// <see cref="GetRenameAsync"/> for progress from there, on its own cadence (§ 3.13: every
    /// 250 ms, matching the surface's repaint tick), and may call <see cref="CancelRenameAsync"/>
    /// at any point in between — this call does not itself wait for the run to finish.
    /// </summary>
    Task<RenameOperationResult> StartRenameAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /session/rename — a pure read, no lock taken. Returns the operation whatever its state,
    /// including a terminal one (<c>succeeded</c>/<c>cancelled</c>/<c>failed</c>); the caller reads
    /// <see cref="RenameOperation.State"/> and <see cref="RenameOperation.Error"/> to decide what to
    /// show. <see cref="RenameOperationResult.NoOperation"/> means no rename has ever been started
    /// in this session — not an error.
    /// </summary>
    Task<RenameOperationResult> GetRenameAsync(CancellationToken ct = default);

    /// <summary>
    /// POST /session/rename/cancel. Idempotent (a second press just returns the current operation).
    /// In <c>preparing</c> this aborts cleanly with nothing touched; in <c>renaming</c> it stops
    /// issuing moves and reunites in place; in <c>saving</c> or later it is too late and this just
    /// reports the state the run is already in.
    /// </summary>
    Task<RenameOperationResult> CancelRenameAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /session — a pure read. Reconnects (and may start the server) if the server is
    /// unreachable; re-opens the same folder if the server answers no_session. Never sends an action.
    /// This is what "Try again" calls after Unknown.
    /// </summary>
    Task<ActionResult> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// DELETE /session. 404 no_session is success. Never throws; a failure is recorded in
    /// LastFailure and otherwise ignored — the next OpenAsync closes whatever is left. Pass a short
    /// token on Esc (PC_CLIENT_PLAN.md § 6.8 says 500 ms) and do not wait for more.
    /// </summary>
    Task CloseAsync(CancellationToken ct = default);
}

public enum LinkState
{
    /// <summary>Nothing has been tried, or the last connect failed. LastFailure says why.</summary>
    Disconnected,
    /// <summary>Authenticated with the server; no session open.</summary>
    Connected,
    /// <summary>A session is open and Snapshot is current as of the last call.</summary>
    InSession,
    /// <summary>A session was open and the last call got no answer. Snapshot is the last known state and still holds its token.</summary>
    InSessionUnreachable,
}

public enum Side { Left, Right }

/// <summary>What ConnectAsync ends with.</summary>
public abstract record ConnectResult
{
    /// <summary>Authenticated. <paramref name="Enrolled"/> is true when a new pairing was made on this call.</summary>
    public sealed record Connected(string BaseUrl, bool Enrolled, bool StartedServer) : ConnectResult;
    public sealed record Failed(Failure Failure) : ConnectResult;
}

/// <summary>What OpenAsync ends with.</summary>
public abstract record OpenResult
{
    /// <summary><paramref name="ReplacedOther"/>: a session on another folder was closed first, silently.</summary>
    public sealed record Opened(Snapshot Snapshot, bool ReplacedOther) : OpenResult;
    public sealed record Failed(Failure Failure) : OpenResult;
}

/// <summary>What every action, save and refresh ends with. Always look at Snapshot first: three of
/// the five cases carry the truth and need nothing said.</summary>
public abstract record ActionResult
{
    /// <summary>The server applied this request and answered 2xx. Snapshot is the new state.</summary>
    public sealed record Applied(Snapshot Snapshot) : ActionResult;

    /// <summary>
    /// The server refused with the current state attached (409 with error.session, or a reopen
    /// after no_session). Nothing this request asked for happened now; Snapshot is the truth.
    /// Usually nothing to say to the owner; <paramref name="Why"/> lets the surface choose.
    /// </summary>
    public sealed record Resynchronised(Snapshot Snapshot, ResyncReason Why) : ActionResult;

    /// <summary>
    /// The server answered and said no, and the owner can do something about it. Snapshot is the
    /// state after a read (may be null if that read also failed). Show Failure.
    /// </summary>
    public sealed record Refused(Failure Failure, Snapshot? Snapshot) : ActionResult;

    /// <summary>
    /// No definite answer after the retry. The link kept the token: the next action the owner asks
    /// for is sent with it, which the server applies once or refuses as stale (§ 13.3). Show Failure
    /// (it says to press again or try again). State is InSessionUnreachable.
    /// </summary>
    public sealed record Unknown(Failure Failure) : ActionResult;

    /// <summary>Nothing was transmitted. Not an error; the surface repaints from Snapshot.</summary>
    public sealed record NotSent(NotSentReason Why) : ActionResult;
}

/// <summary>What StartRenameAsync, GetRenameAsync and CancelRenameAsync each end with.</summary>
public abstract record RenameOperationResult
{
    /// <summary>The server answered with the operation, whatever its state — including a terminal
    /// one. Always look at <see cref="Wire.RenameOperation.State"/> first.</summary>
    public sealed record Observed(RenameOperation Operation) : RenameOperationResult;

    /// <summary>404 no_rename_operation: no rename has ever been started in this session (or the
    /// session was reopened since the last one finished). Not an error.</summary>
    public sealed record NoOperation : RenameOperationResult;

    /// <summary>The server refused with the current state attached (rename_in_progress with the
    /// running operation's id; the session closed underneath it) or answered no, and the owner can
    /// do something about it (rename_failed with the session left untouched — StartRenameAsync
    /// only; the flag is set after the journal write succeeds, never before). Show Failure.</summary>
    public sealed record Refused(Failure Failure) : RenameOperationResult;
}

public enum ResyncReason
{
    /// <summary>lastAction.clientRequestId equals the id this call sent: this exact request landed on an earlier attempt.</summary>
    LandedEarlier,
    /// <summary>lastAction.pairToken equals the token sent but the id differs: an earlier request of ours spent it.</summary>
    TokenSpentByOwnEarlierRequest,
    /// <summary>Neither matched: the pair moved for another reason (the phone, an undo).</summary>
    PairMovedElsewhere,
    /// <summary>lastAction was null: the session was reopened or the server restarted (§ 13.4). The fate of this request is unknown.</summary>
    OutcomeUnknownAfterRestart,
    /// <summary>The server answered no_session; the link reopened the same folder. Cues and session votes restarted.</summary>
    SessionReplaced,
    /// <summary>409 nothing_to_undo: the undo had already been done.</summary>
    UndoAlreadyDone,
    /// <summary>409 no_current_pair: the session is exhausted.</summary>
    NoCurrentPair,
}

public enum NotSentReason { NoSession, PairMoved, Exhausted, UndoNotAvailable }

/// <summary>
/// Something the owner can act on, in words the surface shows as they are. Everything the owner
/// cannot act on is absorbed by the link and never becomes a Failure (§ 5.4).
/// </summary>
/// <param name="Code">The § 5 code, or a client_* synthetic code, for the log. Never shown as the message.</param>
/// <param name="RequestId">The server's X-Request-Id when there was an answer, for the log.</param>
/// <param name="Fatal">True when nothing further can work until the owner acts (wrong server, pairing lost): the surface returns to the start screen.</param>
public sealed record Failure(
    FailureKind Kind,
    string Title,
    string Detail,
    string Code,
    string? RequestId,
    bool Fatal);

public enum FailureKind
{
    ServerNotRunning, NotYourServer, PairingFailed, PairingLost,
    FolderNotFound, FolderNotADirectory, FolderAccessDenied, FolderNotRankable, LibraryJsonUnreadable, FolderLocked,
    SaveFailed, MoveFailed, ServerBusy, ServerShuttingDown, Unreachable, Unexpected,
}
