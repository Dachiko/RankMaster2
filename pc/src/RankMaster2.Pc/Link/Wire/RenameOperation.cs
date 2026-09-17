using System.Text.Json.Serialization;

namespace RankMaster2.Pc.Link.Wire;

/// <summary>
/// SERVER_SPEC.md § 10.16, field for field. The body of <c>POST /session/rename</c> (202),
/// <c>GET /session/rename</c> (200) and <c>POST /session/rename/cancel</c> (200) — always this same
/// shape, whatever the phase. <c>total</c> is the number of files in the plan (one move per file,
/// S-SESSIONS's wire change from <c>2 × N</c>); <c>done</c> reaches it exactly once, on
/// <c>succeeded</c>.
/// </summary>
public sealed record RenameOperation(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("done")] int Done,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("error")] RenameOperationError? Error)
{
    [JsonIgnore] public bool IsRunning => State is "running" or "cancelling";
    [JsonIgnore] public bool IsTerminal => State is "succeeded" or "cancelled" or "failed";
}

/// <summary>Present only when <see cref="RenameOperation.State"/> is <c>"failed"</c>.</summary>
public sealed record RenameOperationError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("reunited")] bool Reunited,
    [property: JsonPropertyName("journal")] string? Journal);
