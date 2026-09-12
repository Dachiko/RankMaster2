using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Media;

/// <summary>
/// The HTTP furniture the media endpoints need: the one error envelope of SERVER_SPEC.md § 4, the
/// headers § 2 puts on every response, and the cache headers § 12.5 puts on media bytes.
/// <para/>
/// The envelope is written here rather than borrowed from a shared exception handler on purpose —
/// the media layer owns its own folder and must not reach into anyone else's. The headers below
/// are all set conditionally, so a server-wide middleware that already set them wins and nothing
/// is written twice.
/// </summary>
public static class MediaHttp
{
    public const string CacheControlImmutable = "private, max-age=31536000, immutable";
    public const string CacheControlNoStore = "no-store";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// § 2: every response carries <c>X-Request-Id</c> and <c>X-Content-Type-Options: nosniff</c>.
    /// The id is whatever a server-wide middleware already put on the response; failing that, the
    /// connection's trace identifier, which is unique per request and opaque.
    /// </summary>
    public static string RequestId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("X-Request-Id", out var existing) && !StringValues.IsNullOrEmpty(existing))
            return existing.ToString();

        if (context.Request.Headers.TryGetValue("X-Request-Id", out var fromClient) && !StringValues.IsNullOrEmpty(fromClient))
        {
            // Echoing a client-supplied id is fine; it is opaque and only ever correlates a log line.
            var echoed = fromClient.ToString();
            if (echoed.Length <= 128)
            {
                context.Response.Headers["X-Request-Id"] = echoed;
                return echoed;
            }
        }

        var id = context.TraceIdentifier;
        context.Response.Headers["X-Request-Id"] = id;
        return id;
    }

    public static void ApplyStandardHeaders(HttpContext context)
    {
        RequestId(context);
        if (!context.Response.Headers.ContainsKey("X-Content-Type-Options"))
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    /// <summary>
    /// Writes the § 4 envelope with the status § 5 binds the code to. <paramref name="session"/> is
    /// the <c>SessionSnapshot</c> when one is open; the media layer never has one to hand, and
    /// § 4 only makes it mandatory for a 409 on a <c>/session/*</c> endpoint, so it stays absent.
    /// </summary>
    public static async Task WriteErrorAsync(
        HttpContext context,
        string code,
        string message,
        object? details = null,
        object? session = null,
        CancellationToken cancellationToken = default)
    {
        ApplyStandardHeaders(context);

        var requestId = RequestId(context);
        context.Response.StatusCode = ErrorCodes.StatusOf(code);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = CacheControlNoStore;

        var envelope = new ApiErrorEnvelope(new ApiError(code, message, requestId, details, session));

        // A HEAD must carry the same status and headers as the GET and no body (§ 2).
        if (HttpMethods.IsHead(context.Request.Method))
            return;

        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The § 12.5 cache headers on a byte response. <c>private</c> is mandatory: these bytes are
    /// the user's photos and no shared cache may hold them.
    /// </summary>
    public static void ApplyByteCacheHeaders(HttpContext context, string etag, bool varyOnAccept)
    {
        ApplyStandardHeaders(context);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = CacheControlImmutable;
        if (varyOnAccept)
            context.Response.Headers.Vary = "Accept";
    }

    /// <summary>
    /// § 12.2: <c>If-None-Match</c> is honoured on all three byte endpoints; an exact match is a
    /// 304, and <c>*</c> matches any existing representation. The comparison is strong — a
    /// <c>W/</c>-prefixed tag never matches an ETag this server issued, because it never issues one.
    /// </summary>
    public static bool IfNoneMatchHits(HttpContext context, string etag)
    {
        if (!context.Request.Headers.TryGetValue("If-None-Match", out var header) || StringValues.IsNullOrEmpty(header))
            return false;

        foreach (var value in header)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var candidate in value.Split(','))
            {
                var tag = candidate.Trim();
                if (tag == "*")
                    return true;
                if (string.Equals(tag, etag, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// § 12.4: an <c>If-Range</c> that matches the ETag lets the range through; one that does not
    /// match yields the whole body with 200. Absent means the range stands on its own.
    /// </summary>
    public static bool IfRangeAllowsRange(HttpContext context, string etag)
    {
        if (!context.Request.Headers.TryGetValue("If-Range", out var header) || StringValues.IsNullOrEmpty(header))
            return true;

        var value = header.ToString().Trim();
        return string.Equals(value, etag, StringComparison.Ordinal);
    }

    public static async Task WriteNotModifiedAsync(HttpContext context, string etag, bool varyOnAccept)
    {
        ApplyByteCacheHeaders(context, etag, varyOnAccept);
        context.Response.StatusCode = StatusCodes.Status304NotModified;
        context.Response.Headers.ContentLength = null;
        await Task.CompletedTask;
    }
}
