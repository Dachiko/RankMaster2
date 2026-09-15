using System.Text.Json;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// Builds responses in the one shape SERVER_SPEC.md § 4 allows. There is no second error shape in
/// this API, so nothing here invents one: every failure goes out as <see cref="ApiErrorEnvelope"/>
/// at the status <see cref="ErrorCodes.StatusOf"/> binds the code to.
/// </summary>
internal static class SessionResults
{
    private const string RequestIdHeader = "X-Request-Id";

    /// <summary>Every JSON response is <c>no-store</c> (§ 2); media is the only cacheable thing.</summary>
    public static IResult Snapshot(HttpContext http, SessionSnapshot snapshot, int status = StatusCodes.Status200OK)
    {
        NoStore(http);
        return Results.Json(snapshot, statusCode: status);
    }

    public static IResult Error(
        HttpContext http,
        string code,
        string message,
        object? details = null,
        SessionSnapshot? session = null)
    {
        NoStore(http);
        var status = ErrorCodes.StatusOf(code);

        // § 6: 503 and 429 must carry Retry-After in delta-seconds.
        if (status is StatusCodes.Status503ServiceUnavailable or StatusCodes.Status429TooManyRequests)
            http.Response.Headers.RetryAfter = "1";

        var envelope = new ApiErrorEnvelope(new ApiError(code, message, RequestIdOf(http), details, session));
        return Results.Json(envelope, statusCode: status);
    }

    /// <summary>
    /// The request id the rest of the server is already using, if any middleware has set one, so a
    /// client sees the same value in <c>X-Request-Id</c> and in <c>error.requestId</c> (§ 4).
    /// </summary>
    public static string RequestIdOf(HttpContext http)
    {
        var existing = http.Response.Headers[RequestIdHeader].ToString();
        if (!string.IsNullOrEmpty(existing))
            return existing;

        if (http.Items.TryGetValue("RequestId", out var item) && item is string fromItems && fromItems.Length > 0)
            return fromItems;

        var generated = http.TraceIdentifier;
        http.Response.Headers[RequestIdHeader] = generated;
        return generated;
    }

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        RequestIdOf(http);
    }
}

/// <summary>
/// A parse failure carried as data rather than an exception, so the endpoints can answer § 5.2
/// codes without a catch around every field read.
/// </summary>
public sealed record BodyError(string Code, string Message, object? Details);

/// <summary>
/// Reads a <c>/session*</c> JSON body under the § 2 and § 15 transport rules: JSON content type
/// only, 65536 bytes maximum, unknown fields ignored, missing and null optional fields treated as
/// absent.
/// </summary>
internal static class SessionBody
{
    public const int MaxBytes = 65536;
    public const int MaxClientRequestId = 64;

    /// <summary>
    /// Returns the parsed document, or a <see cref="BodyError"/>. <paramref name="required"/> is
    /// false only for <c>POST /session/undo</c>, whose body openapi.yaml marks optional.
    /// </summary>
    public static async Task<(JsonElement? Body, BodyError? Error)> ReadAsync(HttpContext http, bool required)
    {
        var contentType = http.Request.ContentType;
        var hasContentType = !string.IsNullOrWhiteSpace(contentType);

        if (hasContentType && !IsJson(contentType!))
        {
            return (null, new BodyError(
                ErrorCodes.UnsupportedContentType,
                "Request bodies must be application/json.",
                null));
        }

        if (!hasContentType && required)
        {
            return (null, new BodyError(
                ErrorCodes.UnsupportedContentType,
                "Request bodies must be application/json.",
                null));
        }

        // § 15: over 65536 bytes is 413. Read one byte past the limit so a body that is exactly at
        // it still passes.
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length));
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                return (null, new BodyError(
                    ErrorCodes.PayloadTooLarge,
                    "The request body is larger than 65536 bytes.",
                    new { maxBytes = MaxBytes }));
            }
        }

        if (buffer.Length == 0)
        {
            if (required)
            {
                return (null, new BodyError(
                    ErrorCodes.InvalidRequest,
                    "The request body is empty.",
                    new { field = "body" }));
            }

            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, new BodyError(
                    ErrorCodes.InvalidRequest,
                    "The request body must be a JSON object.",
                    new { field = "body" }));
            }

            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, new BodyError(
                ErrorCodes.InvalidRequest,
                "The request body is not valid JSON.",
                new { field = "body" }));
        }
    }

    /// <summary>A required string. Absent or null is <c>missing_field</c>; a non-string is <c>invalid_request</c>.</summary>
    public static BodyError? RequiredString(JsonElement? body, string field, out string value)
    {
        value = "";
        if (body is null || !body.Value.TryGetProperty(field, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return new BodyError(ErrorCodes.MissingField, $"'{field}' is required.", new { field });
        }

        if (property.ValueKind != JsonValueKind.String)
            return new BodyError(ErrorCodes.InvalidRequest, $"'{field}' must be a string.", new { field });

        value = property.GetString() ?? "";
        return null;
    }

    /// <summary>
    /// A required side. A non-string is a shape error (§ 5.2 <c>invalid_request</c>); a string that
    /// is neither <c>left</c> nor <c>right</c> is <c>invalid_side</c>, which is what that code exists for.
    /// </summary>
    public static BodyError? RequiredSide(JsonElement? body, string field, out string value)
    {
        var error = RequiredString(body, field, out value);
        if (error is not null)
            return error;

        if (value is not (Sides.Left or Sides.Right))
        {
            return new BodyError(
                ErrorCodes.InvalidSide,
                $"'{field}' must be 'left' or 'right'.",
                new { field, value });
        }

        return null;
    }

    /// <summary>Optional, ≤ 64 characters (§ 15). Absent, null and empty all mean "the client sent none".</summary>
    public static BodyError? OptionalClientRequestId(JsonElement? body, out string? value)
    {
        value = null;
        if (body is null || !body.Value.TryGetProperty("clientRequestId", out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return new BodyError(
                ErrorCodes.InvalidRequest,
                "'clientRequestId' must be a string.",
                new { field = "clientRequestId" });
        }

        var text = property.GetString() ?? "";
        if (text.Length > MaxClientRequestId)
        {
            return new BodyError(
                ErrorCodes.InvalidRequest,
                "'clientRequestId' must be 64 characters or fewer.",
                new { field = "clientRequestId" });
        }

        value = text.Length == 0 ? null : text;
        return null;
    }

    private static bool IsJson(string contentType)
    {
        var semicolon = contentType.IndexOf(';');
        var media = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }
}
