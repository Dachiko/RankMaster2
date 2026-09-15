using System.Text.Json.Serialization;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// SERVER_SPEC.md § 9.1. The one shape every 2xx from a <c>/session*</c> endpoint returns, and the
/// same object <c>error.session</c> carries on a 409. Field for field, with the declared
/// nullability: openapi.yaml marks every property required and <c>additionalProperties: false</c>,
/// so nothing here may be omitted — a null is written as <c>null</c>, never dropped.
/// </summary>
public sealed record SessionSnapshot(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("folderName")] string FolderName,
    [property: JsonPropertyName("policy")] string Policy,
    [property: JsonPropertyName("openedAt")] string OpenedAt,
    [property: JsonPropertyName("prefetchPairs")] int PrefetchPairs,
    [property: JsonPropertyName("sessionVotes")] int SessionVotes,
    [property: JsonPropertyName("counts")] SnapshotCounts Counts,
    [property: JsonPropertyName("progress")] double Progress,
    [property: JsonPropertyName("progressPercent")] int ProgressPercent,
    [property: JsonPropertyName("cues")] IReadOnlyList<string> Cues,
    [property: JsonPropertyName("pair")] SnapshotPair? Pair,
    [property: JsonPropertyName("pairToken")] string? PairToken,
    [property: JsonPropertyName("pairSeq")] ulong PairSeq,
    [property: JsonPropertyName("warmPairs")] IReadOnlyList<SnapshotPair> WarmPairs,
    [property: JsonPropertyName("undoAvailable")] bool UndoAvailable,
    [property: JsonPropertyName("lastAction")] SnapshotLastAction? LastAction,
    [property: JsonPropertyName("lastSavedAt")] string? LastSavedAt);

/// <summary>SERVER_SPEC.md § 9.2. <c>stills + videos == total</c>.</summary>
public sealed record SnapshotCounts(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("rankable")] int Rankable,
    [property: JsonPropertyName("unranked")] int Unranked,
    [property: JsonPropertyName("stills")] int Stills,
    [property: JsonPropertyName("videos")] int Videos);

/// <summary>SERVER_SPEC.md § 9.3. Ratings are deliberately absent.</summary>
public sealed record SnapshotPair(
    [property: JsonPropertyName("left")] SnapshotMediaRef Left,
    [property: JsonPropertyName("right")] SnapshotMediaRef Right);

public sealed record SnapshotMediaRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("mediaVersion")] string? MediaVersion,
    [property: JsonPropertyName("links")] SnapshotMediaLinks Links);

public sealed record SnapshotMediaLinks(
    [property: JsonPropertyName("meta")] string Meta,
    [property: JsonPropertyName("still")] string? Still,
    [property: JsonPropertyName("thumb")] string? Thumb,
    [property: JsonPropertyName("video")] string? Video);

/// <summary>SERVER_SPEC.md § 9.4. Retry disambiguation after a 409 (§ 8.5). In memory only.</summary>
public sealed record SnapshotLastAction(
    [property: JsonPropertyName("seq")] ulong Seq,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("pairToken")] string? PairToken,
    [property: JsonPropertyName("clientRequestId")] string? ClientRequestId,
    [property: JsonPropertyName("winner")] string? Winner,
    [property: JsonPropertyName("side")] string? Side,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("restoredId")] string? RestoredId,
    [property: JsonPropertyName("undoneType")] string? UndoneType,
    [property: JsonPropertyName("at")] string At);

/// <summary>SERVER_SPEC.md § 9.1 / § 9.4 / openapi ActionType. String constants, not enums: the
/// wire values are lowercase and fixed by the contract.</summary>
public static class SessionStates
{
    public const string Ranking = "ranking";
    public const string Exhausted = "exhausted";
}

public static class ActionTypes
{
    public const string Vote = "vote";
    public const string Skip = "skip";
    public const string Discard = "discard";
    public const string Special = "special";
    public const string Undo = "undo";
    public const string DropMissing = "drop_missing";
}

public static class Sides
{
    public const string Left = "left";
    public const string Right = "right";
}
