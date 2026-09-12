using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Security;

/// <summary>
/// Writes the one error envelope from SERVER_SPEC.md § 4 and the JSON success bodies of the
/// endpoints this layer owns. There is deliberately no second error shape anywhere: routing 404s,
/// auth failures and unhandled exceptions all come through here.
/// </summary>
public static class ApiResults
{
    internal const string RequestIdItemKey = "rm2.requestId";
    public const string RequestIdHeader = "X-Request-Id";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The request id assigned by the transport middleware; a fresh one if it never ran.</summary>
    public static string RequestIdOf(HttpContext context)
    {
        if (context.Items.TryGetValue(RequestIdItemKey, out var existing) && existing is string id)
            return id;

        var generated = RequestId.New();
        context.Items[RequestIdItemKey] = generated;
        return generated;
    }

    /// <summary>
    /// Emits an error envelope at the status the code is contractually bound to
    /// (<see cref="ErrorCodes.StatusOf"/>). Nothing else in this server may invent a status for a code.
    /// </summary>
    public static async Task WriteErrorAsync(
        HttpContext context,
        string code,
        string message,
        object? details = null,
        object? session = null,
        int? retryAfterSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var status = ErrorCodes.StatusOf(code);
        var requestId = RequestIdOf(context);

        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers[RequestIdHeader] = requestId;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // SERVER_SPEC.md § 6: 429 and 503 MUST carry Retry-After in delta-seconds.
        if (retryAfterSeconds is { } seconds && (status == 429 || status == 503))
            context.Response.Headers["Retry-After"] = seconds.ToString();

        using var buffer = new MemoryStream(256);
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteString("requestId", requestId);

            if (details is not null)
            {
                writer.WritePropertyName("details");
                JsonSerializer.Serialize(writer, details, Json);
            }

            if (session is not null)
            {
                writer.WritePropertyName("session");
                JsonSerializer.Serialize(writer, session, Json);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>A JSON 200 with the transport headers SERVER_SPEC.md § 2 requires on every response.</summary>
    public static async Task WriteJsonAsync<T>(
        HttpContext context,
        T payload,
        int status = StatusCodes.Status200OK,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers[RequestIdHeader] = RequestIdOf(context);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
    }

    public static Task WriteNoContentAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers[RequestIdHeader] = RequestIdOf(context);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    }
}
