using System.Text.Json;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// A read-only view over a SessionSnapshot (SERVER_SPEC.md § 9.1). It reads the JSON directly
/// rather than deserialising into records, because several of the contract's rules are about the
/// JSON itself — which keys exist, which are null — and a record type would quietly paper over them.
/// </summary>
public sealed class Snapshot(JsonElement json, Rm2Response response)
{
    public JsonElement Json { get; } = json;
    public Rm2Response Response { get; } = response;

    public string SessionId => Json.GetProperty("sessionId").GetString()!;
    public string State => Json.GetProperty("state").GetString()!;
    public string Folder => Json.GetProperty("folder").GetString()!;
    public string FolderName => Json.GetProperty("folderName").GetString()!;
    public string Policy => Json.GetProperty("policy").GetString()!;
    public int PrefetchPairs => Json.GetProperty("prefetchPairs").GetInt32();
    public int SessionVotes => Json.GetProperty("sessionVotes").GetInt32();
    public long PairSeq => Json.GetProperty("pairSeq").GetInt64();
    public bool UndoAvailable => Json.GetProperty("undoAvailable").GetBoolean();
    public double Progress => Json.GetProperty("progress").GetDouble();
    public int ProgressPercent => Json.GetProperty("progressPercent").GetInt32();

    public bool IsExhausted => State == "exhausted";
    public bool IsRanking => State == "ranking";

    public string? PairToken => Json.GetProperty("pairToken").ValueKind == JsonValueKind.Null
        ? null
        : Json.GetProperty("pairToken").GetString();

    public string? LastSavedAt => Json.GetProperty("lastSavedAt").ValueKind == JsonValueKind.Null
        ? null
        : Json.GetProperty("lastSavedAt").GetString();

    public string[] Cues => Json.GetProperty("cues").EnumerateArray().Select(c => c.GetString()!).ToArray();

    public int Total => Json.GetProperty("counts").GetProperty("total").GetInt32();
    public int Rankable => Json.GetProperty("counts").GetProperty("rankable").GetInt32();
    public int Unranked => Json.GetProperty("counts").GetProperty("unranked").GetInt32();
    public int Stills => Json.GetProperty("counts").GetProperty("stills").GetInt32();
    public int Videos => Json.GetProperty("counts").GetProperty("videos").GetInt32();

    public JsonElement? PairJson =>
        Json.GetProperty("pair").ValueKind == JsonValueKind.Null ? null : Json.GetProperty("pair");

    public MediaRef Left => RequirePair().Left;
    public MediaRef Right => RequirePair().Right;

    public MediaRef Side(string side) => side == "left" ? Left : Right;

    public (MediaRef Left, MediaRef Right) RequirePair()
    {
        var pair = PairJson ?? throw Response.Failure(
            "This snapshot has no pair: state is 'exhausted'. The test expected a pair to act on.");
        return (new MediaRef(pair.GetProperty("left")), new MediaRef(pair.GetProperty("right")));
    }

    public string RequireToken(string clause) =>
        PairToken ?? throw Response.Failure($"{clause}: expected a pairToken, but the session is exhausted.");

    public MediaRef[] WarmPairMembers =>
        Json.GetProperty("warmPairs").EnumerateArray()
            .SelectMany(p => new[] { new MediaRef(p.GetProperty("left")), new MediaRef(p.GetProperty("right")) })
            .ToArray();

    public int WarmPairCount => Json.GetProperty("warmPairs").GetArrayLength();

    public string[] PairIds => PairJson is { } pair
        ? new[] { pair.GetProperty("left").GetProperty("id").GetString()!, pair.GetProperty("right").GetProperty("id").GetString()! }
        : Array.Empty<string>();

    public LastAction? LastAction =>
        Json.GetProperty("lastAction").ValueKind == JsonValueKind.Null
            ? null
            : new LastAction(Json.GetProperty("lastAction"));

    public LastAction RequireLastAction(string clause) =>
        LastAction ?? throw Response.Failure(
            $"{clause}: lastAction is null. SERVER_SPEC.md § 9.4 records the most recent successful " +
            "mutation there, and § 8.5 makes it the only way a client can tell whether a lost request landed.");
}

public sealed class MediaRef(JsonElement json)
{
    public JsonElement Json { get; } = json;

    public string Id => Json.GetProperty("id").GetString()!;
    public string Kind => Json.GetProperty("kind").GetString()!;
    public bool IsStill => Kind == "still";

    public long? SizeBytes => Json.GetProperty("sizeBytes").ValueKind == JsonValueKind.Null
        ? null
        : Json.GetProperty("sizeBytes").GetInt64();

    public string? MediaVersion => Json.GetProperty("mediaVersion").ValueKind == JsonValueKind.Null
        ? null
        : Json.GetProperty("mediaVersion").GetString();

    public string MetaLink => Json.GetProperty("links").GetProperty("meta").GetString()!;
    public string? StillLink => Link("still");
    public string? ThumbLink => Link("thumb");
    public string? VideoLink => Link("video");

    private string? Link(string name)
    {
        var value = Json.GetProperty("links").GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    public override string ToString() => $"{Id} ({Kind})";
}

public sealed class LastAction(JsonElement json)
{
    public JsonElement Json { get; } = json;

    public long Seq => Json.GetProperty("seq").GetInt64();
    public string Type => Json.GetProperty("type").GetString()!;
    public string? PairToken => Text("pairToken");
    public string? ClientRequestId => Text("clientRequestId");
    public string? Winner => Text("winner");
    public string? SideValue => Text("side");
    public string? Id => Text("id");
    public string? RestoredId => Text("restoredId");
    public string? UndoneType => Text("undoneType");
    public string At => Json.GetProperty("at").GetString()!;

    private string? Text(string name)
    {
        var value = Json.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    public override string ToString() => $"{Type}@{Seq}";
}
