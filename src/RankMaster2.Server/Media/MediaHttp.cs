using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
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
    /// <para/>
    /// The id is whatever a server-wide middleware already put on the <b>response</b>; failing
    /// that, the connection's trace identifier, which is opaque and unique per request. A
    /// client-supplied <c>X-Request-Id</c> is deliberately not echoed: § 4 has the id identify one
    /// request in the server's own log, and a value the caller chooses can neither be trusted to
    /// be unique nor to be safe to write there.
    /// </summary>
    public static string RequestId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("X-Request-Id", out var existing) && !StringValues.IsNullOrEmpty(existing))
            return existing.ToString();

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
    /// the <c>SessionSnapshot</c> when one is open; the media layer never has one to hand. This is
    /// deliberate, not a gap: § 4, as amended (C2), only makes <c>session</c> mandatory for a 409
    /// on a <c>/session/*</c> endpoint, and says a nullable field MAY be omitted when null — every
    /// <c>/media/*</c> error carries <c>session: null</c> (or omits it), never a lock-free snapshot
    /// built for a 404 nobody reads.
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
    /// <para/>
    /// RFC 9110 § 13.1.5 lets a client send either an entity-tag or an HTTP-date in <c>If-Range</c>,
    /// and this server never advertises a <c>Last-Modified</c> for a compliant client to have
    /// echoed back — but a video's actual on-disk modification time is exactly what
    /// <paramref name="lastModifiedUtc"/> is when the caller has it (§ 12.2 already hashes the same
    /// timestamp into the ETag), so a date-form <c>If-Range</c> is honoured against it rather than
    /// always falling through to a full re-download (AUDIT2.md § 3.13). When
    /// <paramref name="lastModifiedUtc"/> is not supplied, only the entity-tag form is understood,
    /// exactly as before.
    /// </summary>
    public static bool IfRangeAllowsRange(HttpContext context, string etag, DateTimeOffset? lastModifiedUtc = null)
    {
        if (!context.Request.Headers.TryGetValue("If-Range", out var header) || StringValues.IsNullOrEmpty(header))
            return true;

        var value = header.ToString().Trim();

        // An entity-tag is always a DQUOTE-delimited token (§ 12.2: this server's ETags are always
        // quoted and always strong); anything else that parses as an HTTP-date is the date form.
        if (value.Length == 0 || value[0] != '"')
        {
            if (lastModifiedUtc is { } modified && HeaderUtilities.TryParseDate(value, out var since))
            {
                // HTTP-date has one-second resolution (RFC 9110 § 5.6.7), so truncate the file's
                // own timestamp to the second before comparing — otherwise a file untouched since
                // the very second named in the header would wrongly appear "newer" than it.
                var truncated = new DateTimeOffset(
                    modified.UtcDateTime.Ticks - modified.UtcDateTime.Ticks % TimeSpan.TicksPerSecond,
                    TimeSpan.Zero);
                return truncated <= since;
            }
        }

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
