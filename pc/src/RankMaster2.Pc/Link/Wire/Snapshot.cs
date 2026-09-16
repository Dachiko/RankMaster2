using System.Text.Json.Serialization;

namespace RankMaster2.Pc.Link.Wire;

/// <summary>
/// SERVER_SPEC.md § 9.1, field for field. Every 2xx JSON response from the session endpoints, and
/// every 409's <c>error.session</c>, is this same shape.
///
/// <see cref="PairToken"/> exists because this is a faithful mirror of the wire, and because
/// <c>Resynchronised.Why</c> (see <see cref="ISessionLink"/>) is computed from it and from
/// <see cref="Wire.LastAction.PairToken"/>. There is no member anywhere in the public API of
/// <c>RankMaster2.Pc.Link</c> that accepts a token — that is the property that matters, and test N5
/// asserts it by reflection.
/// </summary>
public sealed record Snapshot(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("folderName")] string FolderName,
    [property: JsonPropertyName("policy")] string Policy,
    [property: JsonPropertyName("openedAt")] string OpenedAt,
    [property: JsonPropertyName("prefetchPairs")] int PrefetchPairs,
    [property: JsonPropertyName("sessionVotes")] int SessionVotes,
    [property: JsonPropertyName("counts")] Counts Counts,
    [property: JsonPropertyName("progress")] double Progress,
    [property: JsonPropertyName("progressPercent")] int ProgressPercent,
    [property: JsonPropertyName("cues")] IReadOnlyList<string> Cues,
    [property: JsonPropertyName("pair")] Pair? Pair,
    [property: JsonPropertyName("pairToken")] string? PairToken,
    [property: JsonPropertyName("pairSeq")] long PairSeq,
    [property: JsonPropertyName("warmPairs")] IReadOnlyList<Pair> WarmPairs,
    [property: JsonPropertyName("undoAvailable")] bool UndoAvailable,
    [property: JsonPropertyName("lastAction")] LastAction? LastAction,
    [property: JsonPropertyName("lastSavedAt")] string? LastSavedAt)
{
    public bool IsRanking => State == "ranking";
    public bool IsExhausted => State == "exhausted";
}

public sealed record Counts(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("rankable")] int Rankable,
    [property: JsonPropertyName("unranked")] int Unranked,
    [property: JsonPropertyName("stills")] int Stills,
    [property: JsonPropertyName("videos")] int Videos);

public sealed record Pair(
    [property: JsonPropertyName("left")] MediaRef Left,
    [property: JsonPropertyName("right")] MediaRef Right);

public sealed record MediaRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("mediaVersion")] string? MediaVersion,
    [property: JsonPropertyName("links")] Links Links)
{
    public bool IsStill => Kind == "still";
    public bool IsVideo => Kind == "video";

    /// <summary>§ 11.3: the file has gone from the folder under the session. The only action left is
    /// discard, which the server turns into drop_missing.</summary>
    public bool IsMissing => SizeBytes is null;
}

/// <summary>§ 9.3. Carried through unused: the PC reads pixels from disk. Never rebuilt, never parsed.</summary>
public sealed record Links(
    [property: JsonPropertyName("meta")] string Meta,
    [property: JsonPropertyName("still")] string? Still,
    [property: JsonPropertyName("thumb")] string? Thumb,
    [property: JsonPropertyName("video")] string? Video);

public sealed record LastAction(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("pairToken")] string? PairToken,
    [property: JsonPropertyName("clientRequestId")] string? ClientRequestId,
    [property: JsonPropertyName("winner")] string? Winner,
    [property: JsonPropertyName("side")] string? Side,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("restoredId")] string? RestoredId,
    [property: JsonPropertyName("undoneType")] string? UndoneType,
    [property: JsonPropertyName("at")] string At);

public static class ActionTypes
{
    public const string Vote = "vote";
    public const string Skip = "skip";
    public const string Discard = "discard";
    public const string Special = "special";
    public const string Undo = "undo";
    public const string DropMissing = "drop_missing";
}
