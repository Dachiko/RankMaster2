namespace RankMaster2.Server.Sessions;

/// <summary>
/// What a registry call produced, before it is turned into HTTP. Keeping it as data means the
/// ordering rules of SERVER_SPEC.md § 8.4 live in one place — inside the session lock — instead of
/// being spread across nine endpoint handlers.
/// </summary>
public sealed record SessionOutcome
{
    private SessionOutcome()
    {
    }

    public int Status { get; private init; }

    public SessionSnapshot? Snapshot { get; private init; }

    /// <summary>
    /// § 10.17: set only by <c>GET /session/items</c>, whose body is the snapshot plus this list.
    /// Null for every other outcome.
    /// </summary>
    public IReadOnlyList<SnapshotMediaRef>? Items { get; private init; }

    /// <summary>
    /// § 10.17 / § 10.19: the stored review position (null = none). Carried by <c>GET /session/items</c>
    /// next to <see cref="Items"/>, and by itself, with no snapshot, by <c>PUT /session/review-position</c>
    /// (<see cref="IsReviewPositionOnly"/>).
    /// </summary>
    public string? ReviewPosition { get; private init; }

    /// <summary>§ 10.19: the 200 body is just <c>{ "reviewPosition": … }</c>, not a snapshot.</summary>
    public bool IsReviewPositionOnly { get; private init; }

    public string? ErrorCode { get; private init; }

    public string? ErrorMessage { get; private init; }

    public object? ErrorDetails { get; private init; }

    /// <summary>
    /// § 4: present iff a session is open when the error is produced, and required for every 409 on
    /// a <c>/session*</c> endpoint.
    /// </summary>
    public SessionSnapshot? ErrorSession { get; private init; }

    public bool IsError => ErrorCode is not null;

    public static SessionOutcome Ok(
        SessionSnapshot snapshot,
        int status = StatusCodes.Status200OK,
        IReadOnlyList<SnapshotMediaRef>? items = null,
        string? reviewPosition = null) =>
        new() { Status = status, Snapshot = snapshot, Items = items, ReviewPosition = reviewPosition };

    /// <summary>§ 10.19: the answer to <c>PUT /session/review-position</c>.</summary>
    public static SessionOutcome ReviewPositionOnly(string? reviewPosition) =>
        new() { Status = StatusCodes.Status200OK, ReviewPosition = reviewPosition, IsReviewPositionOnly = true };

    public static SessionOutcome NoContent() =>
        new() { Status = StatusCodes.Status204NoContent };

    public static SessionOutcome Fail(
        string code,
        string message,
        object? details = null,
        SessionSnapshot? session = null) =>
        new()
        {
            ErrorCode = code,
            ErrorMessage = message,
            ErrorDetails = details,
            ErrorSession = session,
        };
}
