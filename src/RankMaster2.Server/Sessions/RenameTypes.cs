using System.Globalization;
using System.Text.Json.Serialization;
using RankMaster2.Catalog;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Sessions;

/// <summary>SERVER_SPEC.md § 10.16 / § 3.4 of pc/plans/F-server-rename.md. The wire shape of the
/// server's one long-running operation: started by <c>POST /session/rename</c>, observed by
/// <c>GET</c>, stopped by <c>POST /session/rename/cancel</c>.</summary>
public sealed record RenameOperation(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("done")] int Done,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("error")] RenameOperationError? Error);

public sealed record RenameOperationError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("reunited")] bool Reunited,
    [property: JsonPropertyName("journal")] string? Journal);

public static class RenameStates
{
    public const string Running = "running";
    public const string Cancelling = "cancelling";
    public const string Succeeded = "succeeded";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public static class RenamePhases
{
    public const string Preparing = "preparing";
    public const string Renaming = "renaming";
    public const string Saving = "saving";
    public const string Reuniting = "reuniting";
    public const string Done = "done";
}

/// <summary>
/// Parallel to <see cref="SessionOutcome"/>, but for the <c>/session/rename*</c> group: the 2xx body
/// is a <see cref="RenameOperation"/>, never a <see cref="SessionSnapshot"/> — the operation is a
/// separate small resource, and <c>/session</c> keeps its one shape (§ 3.4).
/// </summary>
public sealed record RenameOutcome
{
    private RenameOutcome()
    {
    }

    public int Status { get; private init; }

    public RenameOperation? Operation { get; private init; }

    public string? ErrorCode { get; private init; }

    public string? ErrorMessage { get; private init; }

    public object? ErrorDetails { get; private init; }

    /// <summary>§ 4: present iff a session is open when the error is produced.</summary>
    public SessionSnapshot? ErrorSession { get; private init; }

    public bool IsError => ErrorCode is not null;

    public static RenameOutcome Ok(RenameOperation operation, int status = StatusCodes.Status200OK) =>
        new() { Status = status, Operation = operation };

    public static RenameOutcome Fail(
        string code, string message, object? details = null, SessionSnapshot? session = null) =>
        new() { ErrorCode = code, ErrorMessage = message, ErrorDetails = details, ErrorSession = session };
}

/// <summary>
/// Writes the journal. The default just calls <see cref="RenameEngine.WriteJournal"/>; a test may
/// inject one that blocks on a signal to hold the operation in <c>preparing</c> deterministically —
/// the same seam this codebase already uses for <c>ICatalog</c> to hold a run in <c>saving</c>
/// (pc/plans/F-server-rename.md § 6). <c>SessionRegistry.StartRenameAsync</c> publishes the
/// <see cref="RenameRun"/> before calling this, so a concurrent cancel can flag it while this call
/// is still blocked, and the run aborts cleanly the moment it returns.
/// </summary>
public interface IRenameJournalWriter
{
    void Write(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt);
}

public sealed class DefaultRenameJournalWriter : IRenameJournalWriter
{
    public void Write(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt) =>
        RenameEngine.WriteJournal(folder, plan, createdAt);
}

/// <summary>
/// The one rename operation's mutable state. Everything here is guarded by a private lock that is
/// held only for field reads/writes, never across I/O — <c>GET /session/rename</c> must stay
/// lock-free with respect to the session semaphore (§ 3.4), and this is the only synchronisation it
/// needs.
/// </summary>
public sealed class RenameRun
{
    private readonly object _sync = new();

    public RenameRun(
        string operationId, string folder, IReadOnlyList<MediaRecord> preRenameRecords, DateTimeOffset startedAt)
    {
        OperationId = operationId;
        Folder = folder;
        PreRenameRecords = preRenameRecords;
        StartedAt = startedAt;
        _updatedAt = startedAt;
    }

    public string OperationId { get; }
    public string Folder { get; }
    public IReadOnlyList<MediaRecord> PreRenameRecords { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Set once the plan is known (after <see cref="RenameEngine.BuildPlan"/>).</summary>
    public IReadOnlyList<PlanEntry> Plan { get; set; } = Array.Empty<PlanEntry>();

    /// <summary>
    /// SERVER_SPEC.md § 10.16: the optional <c>clientRequestId</c> from the start request, echoed in
    /// <c>lastAction</c> when the run succeeds (§ 9.4). Null when the client sent none. It is not on
    /// the operation's own wire shape — the operation has its own id.
    /// </summary>
    public string? ClientRequestId { get; init; }

    /// <summary>
    /// Polled between every file move (§ 3.3 "checking for cancel between files"). Plain volatile
    /// read/write: it is only ever set true, never back to false, so a torn read costs nothing.
    /// </summary>
    public volatile bool CancelRequested;

    private string _state = RenameStates.Running;
    private string _phase = RenamePhases.Preparing;
    private int _done;
    private int _total;
    private DateTimeOffset _updatedAt;
    private string? _errorCode;
    private bool _errorReunited;
    private string? _errorJournal;

    public void RequestCancel()
    {
        lock (_sync)
        {
            CancelRequested = true;
            // "too late" (§ 3.5): once saving has started (or the run is already terminal), cancel
            // changes nothing and just reports the current state.
            if (_state == RenameStates.Running && _phase is RenamePhases.Preparing or RenamePhases.Renaming)
            {
                _state = RenameStates.Cancelling;
                _updatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public void MarkPreparingDone(int total)
    {
        lock (_sync)
        {
            _phase = RenamePhases.Renaming;
            _total = total;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReportProgress(string phase, int done)
    {
        lock (_sync)
        {
            _phase = phase;
            _done = done;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReportPhase(string phase)
    {
        lock (_sync)
        {
            _phase = phase;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Succeed()
    {
        lock (_sync)
        {
            _state = RenameStates.Succeeded;
            _phase = RenamePhases.Done;
            _done = _total;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Cleanly aborted before any file moved (§ 3.5, "preparing"). Session untouched.</summary>
    public void AbortBeforeAnyMove()
    {
        lock (_sync)
        {
            _state = RenameStates.Cancelled;
            _phase = RenamePhases.Done;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Stopped mid-renaming and reunited in place (§ 3.5). The folder may be half-renamed.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _state = RenameStates.Cancelled;
            _phase = RenamePhases.Done;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(bool reunited, string? journal)
    {
        lock (_sync)
        {
            _state = RenameStates.Failed;
            _phase = RenamePhases.Done;
            _errorCode = Contracts.ErrorCodes.RenameFailed;
            _errorReunited = reunited;
            _errorJournal = journal;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public RenameOperation ToWire()
    {
        lock (_sync)
        {
            return new RenameOperation(
                OperationId, _state, _phase, _done, _total,
                Rfc3339(StartedAt), Rfc3339(_updatedAt),
                _errorCode is null ? null : new RenameOperationError(_errorCode, _errorReunited, _errorJournal));
        }
    }

    private static string Rfc3339(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
