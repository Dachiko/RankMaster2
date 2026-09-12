using System.Text.Json;

namespace RankMaster2.Cli;

/// <summary>
/// A reader for the one object every successful <c>/session*</c> call returns
/// (SERVER_SPEC.md § 9.1). Reads the JSON directly: several of the contract's rules are about which
/// keys are present and which are null, and deserialising into a record would hide exactly those.
/// </summary>
public sealed class Snapshot(JsonElement json)
{
    public JsonElement Json { get; } = json;

    public static Snapshot? From(Reply reply) =>
        reply.Json is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("sessionId", out _)
            ? new Snapshot(json)
            : null;

    /// <summary>The snapshot embedded in a 409 (§ 4, § 8.5), which resynchronises a client in one round trip.</summary>
    public static Snapshot? FromError(Reply reply) =>
        reply.Json is { } json && json.TryGetProperty("error", out var error) &&
        error.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object
            ? new Snapshot(session)
            : null;

    private JsonElement Get(string name) => Json.GetProperty(name);
    private string? Text(string name) =>
        Json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public string SessionId => Text("sessionId") ?? "";
    public string State => Text("state") ?? "";
    public string Folder => Text("folder") ?? "";
    public string FolderName => Text("folderName") ?? "";
    public string Policy => Text("policy") ?? "";
    public string? PairToken => Text("pairToken");
    public string? LastSavedAt => Text("lastSavedAt");

    public long PairSeq => Get("pairSeq").GetInt64();
    public int SessionVotes => Get("sessionVotes").GetInt32();
    public int PrefetchPairs => Get("prefetchPairs").GetInt32();
    public bool UndoAvailable => Get("undoAvailable").GetBoolean();
    public int ProgressPercent => Get("progressPercent").GetInt32();

    public bool IsExhausted => State == "exhausted";

    public int Total => Get("counts").GetProperty("total").GetInt32();
    public int Rankable => Get("counts").GetProperty("rankable").GetInt32();
    public int Unranked => Get("counts").GetProperty("unranked").GetInt32();
    public int Stills => Get("counts").GetProperty("stills").GetInt32();
    public int Videos => Get("counts").GetProperty("videos").GetInt32();

    public string[] Cues => Get("cues").EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
    public int WarmPairCount => Get("warmPairs").GetArrayLength();

    public MediaRef? Left => Pair is { } pair ? new MediaRef(pair.GetProperty("left")) : null;
    public MediaRef? Right => Pair is { } pair ? new MediaRef(pair.GetProperty("right")) : null;

    public JsonElement? Pair =>
        Get("pair").ValueKind == JsonValueKind.Null ? null : Get("pair");

    public JsonElement? LastAction =>
        Get("lastAction").ValueKind == JsonValueKind.Null ? null : Get("lastAction");

    public string? LastActionType =>
        LastAction is { } action && action.TryGetProperty("type", out var type) ? type.GetString() : null;

    public string? LastActionField(string name) =>
        LastAction is { } action && action.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The one-line form the journal prints after every mutation.</summary>
    public string Describe()
    {
        var pair = Pair is null
            ? "no pair (exhausted)"
            : $"{Left!.Id}  vs  {Right!.Id}";

        return $"seq {PairSeq}  {pair}\n" +
               $"token {PairToken ?? "(null)"}  votes {SessionVotes}  " +
               $"{Total} files ({Rankable} rankable, {Unranked} unranked)  " +
               $"progress {ProgressPercent}%  warm {WarmPairCount}  undo {(UndoAvailable ? "available" : "none")}" +
               (Cues.Length > 0 ? $"\ncues [{string.Join(", ", Cues)}]" : "");
    }
}

public sealed class MediaRef(JsonElement json)
{
    public JsonElement Json { get; } = json;

    public string Id => Json.GetProperty("id").GetString() ?? "";
    public string Kind => Json.GetProperty("kind").GetString() ?? "";

    public long? SizeBytes => Json.GetProperty("sizeBytes").ValueKind == JsonValueKind.Null
        ? null
        : Json.GetProperty("sizeBytes").GetInt64();

    public string? MediaVersion => Json.GetProperty("mediaVersion").ValueKind == JsonValueKind.Null
        ? null
        : Json.GetProperty("mediaVersion").GetString();

    private string? Link(string name)
    {
        var value = Json.GetProperty("links").GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    public string? MetaLink => Link("meta");
    public string? StillLink => Link("still");
    public string? ThumbLink => Link("thumb");
    public string? VideoLink => Link("video");

    public override string ToString() => $"{Id} ({Kind}, {SizeBytes?.ToString() ?? "?"} B)";
}
