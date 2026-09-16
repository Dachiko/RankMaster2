using System.Text.Json;
using System.Text.Json.Serialization;

namespace RankMaster2.Pc.Link.Wire;

/// <summary>
/// Source-generated so startup pays no reflection warm-up for the wire types (`PC_CLIENT_PLAN.md`
/// § 3's launch budget). Unknown members are ignored by default — the server is free to add fields
/// (SERVER_SPEC.md § 2) — and every property carries an explicit <see cref="JsonPropertyNameAttribute"/>
/// so the naming policy below is a formality, not something a future property can get wrong silently.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(Counts))]
[JsonSerializable(typeof(Pair))]
[JsonSerializable(typeof(MediaRef))]
[JsonSerializable(typeof(Links))]
[JsonSerializable(typeof(LastAction))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(PingSession))]
[JsonSerializable(typeof(PairedDevice))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(OpenSessionRequest))]
[JsonSerializable(typeof(VoteRequest))]
[JsonSerializable(typeof(SkipRequest))]
[JsonSerializable(typeof(SideActionRequest))]
[JsonSerializable(typeof(CancelRequest))]
[JsonSerializable(typeof(SaveRequest))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class WireJsonContext : JsonSerializerContext
{
}

/// <summary>The one <see cref="JsonSerializerOptions"/> every call in <c>Link/</c> serialises with.</summary>
internal static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        TypeInfoResolver = WireJsonContext.Default,
    };
}
