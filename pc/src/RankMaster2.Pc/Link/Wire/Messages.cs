using System.Text.Json.Serialization;

namespace RankMaster2.Pc.Link.Wire;

// ---- public: GET /ping -------------------------------------------------------------------------

public sealed record Ping(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("authenticated")] bool Authenticated,
    [property: JsonPropertyName("certificateFingerprint")] string CertificateFingerprint,
    [property: JsonPropertyName("serverTime")] string ServerTime,
    [property: JsonPropertyName("session")] PingSession? Session);

public sealed record PingSession(
    [property: JsonPropertyName("open")] bool Open,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("folder")] string? Folder,
    [property: JsonPropertyName("state")] string? State);

// ---- public: POST /pair -------------------------------------------------------------------------

public sealed record PairedDevice(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string? DeviceName,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("issuedAt")] string IssuedAt,
    [property: JsonPropertyName("expiresAt")] string? ExpiresAt);

// ---- public: the § 4 error envelope --------------------------------------------------------------

public sealed record ErrorEnvelope(
    [property: JsonPropertyName("error")] ErrorBody Error);

public sealed record ErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("details")] System.Text.Json.JsonElement? Details,
    [property: JsonPropertyName("session")] Snapshot? Session);

// ---- internal: request bodies --------------------------------------------------------------------
// Declared as types, not anonymous objects, so `pairToken` and `clientRequestId` cannot be forgotten
// on an action — RankMaster2.Pc.Link.Wire.Messages mirrors Rm2Wire.kt's reasoning.

internal sealed record OpenSessionRequest(
    [property: JsonPropertyName("folder")] string Folder);

internal sealed record VoteRequest(
    [property: JsonPropertyName("pairToken")] string PairToken,
    [property: JsonPropertyName("winner")] string Winner,
    [property: JsonPropertyName("clientRequestId")] string ClientRequestId);

internal sealed record SkipRequest(
    [property: JsonPropertyName("pairToken")] string PairToken,
    [property: JsonPropertyName("clientRequestId")] string ClientRequestId);

internal sealed record SideActionRequest(
    [property: JsonPropertyName("pairToken")] string PairToken,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("clientRequestId")] string ClientRequestId);

internal sealed record CancelRequest(
    [property: JsonPropertyName("clientRequestId")] string ClientRequestId);

internal sealed record SaveRequest();

internal sealed record PairRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("deviceName")] string DeviceName);
