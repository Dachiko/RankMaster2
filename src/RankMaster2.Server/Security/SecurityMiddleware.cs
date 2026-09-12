using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Security;

/// <summary>
/// The transport and authentication pipeline. Two pieces of middleware, in this order:
///
/// <list type="number">
/// <item><b>Transport.</b> Assigns the request id, puts the headers SERVER_SPEC.md § 2 requires on
///   every response, converts an unhandled exception into a generic <c>500 internal_error</c>, and
///   converts a bodyless 404 or 405 from the routing layer into the one error envelope. Nothing in
///   this server is allowed to answer in a second shape.</item>
/// <item><b>Gate.</b> Readiness, request-body limits, the pairing rate limit, and authentication.
///   It is an allow-list: <c>GET /ping</c> and <c>POST /pair</c> are anonymous and
///   <b>everything else requires a valid bearer token</b> — including routes that do not exist yet
///   and routes another folder adds later. A new endpoint is authenticated by default; forgetting
///   to protect one is not a thing that can happen here.</item>
/// </list>
/// </summary>
internal static class SecurityMiddleware
{
    private const int MaxJsonBodyBytes = 65536;
    private const string ApiBase = "/api/v1";
    private const string DeviceItemKey = "rm2.device";

    public static DeviceRecord? AuthenticatedDevice(HttpContext context) =>
        context.Items.TryGetValue(DeviceItemKey, out var value) ? value as DeviceRecord : null;

    public static void UseTransport(this WebApplication app, SecurityState state, ILogger logger)
    {
        app.Use(async (context, next) =>
        {
            var requestId = RequestId.New();
            context.Items[ApiResults.RequestIdItemKey] = requestId;
            context.TraceIdentifier = requestId;

            context.Response.OnStarting(static s =>
            {
                var ctx = (HttpContext)s;
                var headers = ctx.Response.Headers;

                if (!headers.ContainsKey(ApiResults.RequestIdHeader))
                    headers[ApiResults.RequestIdHeader] = ApiResults.RequestIdOf(ctx);

                headers["X-Content-Type-Options"] = "nosniff";

                // Every JSON response is no-store (§ 2). Media bytes are the only cacheable
                // responses and set their own Cache-Control, which is never overwritten here.
                var contentType = headers.ContentType.ToString();
                if (!headers.ContainsKey("Cache-Control") &&
                    contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    headers["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            }, context);

            try
            {
                await next(context);
            }
            catch (BadHttpRequestException e)
            {
                // Kestrel rejected the body before any handler saw it.
                if (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.PayloadTooLarge,
                        "The JSON body is larger than the 65536 byte limit.",
                        new { maxBytes = MaxJsonBodyBytes });
                }
                else
                {
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidRequest,
                        "The request could not be read.", new { field = "body" });
                }

                return;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The client hung up. Nothing to say to nobody.
                return;
            }
            catch (Exception e)
            {
                // SERVER_SPEC.md § 4: the detail goes to the log keyed by requestId, never to the
                // client. A stack trace names assemblies, versions and absolute source paths.
                logger.LogError(e, "Unhandled exception on {Method} {Path} (requestId {RequestId})",
                    context.Request.Method, context.Request.Path, requestId);

                await ApiResults.WriteErrorAsync(context, ErrorCodes.InternalError,
                    "The server failed to handle the request.");
                return;
            }

            // A 404 or 405 produced by routing carries no body. Give it the envelope; a client that
            // branches on error.code must never meet a response that has no code at all.
            if (!context.Response.HasStarted &&
                context.Response.ContentLength is null or 0 &&
                context.Response.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed)
            {
                await ApiResults.WriteErrorAsync(context, ErrorCodes.NotFound, "No such route.");
            }
        });
    }

    public static void UseGate(this WebApplication app, SecurityState state)
    {
        app.Use(async (context, next) =>
        {
            var path = NormalisePath(context.Request.Path.Value);
            var method = context.Request.Method;
            var now = DateTimeOffset.UtcNow;

            var isPing = path.Equals(ApiBase + "/ping", StringComparison.OrdinalIgnoreCase);
            var isPair = path.Equals(ApiBase + "/pair", StringComparison.OrdinalIgnoreCase) &&
                         HttpMethods.IsPost(method);

            // § 14: while starting or shutting down only /ping answers.
            if (!state.Ready && !isPing)
            {
                await ApiResults.WriteErrorAsync(context, ErrorCodes.ServerShuttingDown,
                    "The server is not accepting requests.",
                    new { retryAfterSeconds = 5 }, retryAfterSeconds: 5);
                return;
            }

            // The rate limit is charged before the body is looked at, so a flood of malformed
            // pairing requests costs the attacker its budget just as a flood of guesses does.
            if (isPair)
            {
                var key = PairingRateLimiter.KeyFor(context.Connection.RemoteIpAddress);
                if (state.Pairing.ChargeRateLimit(key, now) is { } retryAfter)
                {
                    await ApiResults.WriteErrorAsync(context, ErrorCodes.TooManyRequests,
                        "Too many pairing attempts from this address.",
                        new { retryAfterSeconds = retryAfter }, retryAfterSeconds: retryAfter);
                    return;
                }
            }

            if (!await CheckBodyAsync(context)) return;

            if (!isPair && !await AuthenticateAsync(context, state, isPing, now)) return;

            await next(context);
        });
    }

    /// <summary>
    /// SERVER_SPEC.md § 2: a JSON request body is <c>application/json</c> and at most 65536 bytes.
    /// Applied to every method that can carry a body, so a route added later inherits the limit.
    /// </summary>
    private static async Task<bool> CheckBodyAsync(HttpContext context)
    {
        var method = context.Request.Method;

        // The size cap applies to any method that carried a body, including one that had no
        // business carrying it. The Content-Type rule applies only to the methods that are
        // supposed to send JSON, so a stray body on a DELETE is ignored rather than turned into
        // a 415 the contract never mentions.
        var expectsJson = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

        var declared = context.Request.ContentLength;
        var chunked = declared is null &&
                      context.Request.Headers.TransferEncoding.ToString()
                          .Contains("chunked", StringComparison.OrdinalIgnoreCase);

        if (declared is null && !chunked) return true;   // genuinely bodyless POST
        if (declared == 0) return true;

        if (declared > MaxJsonBodyBytes)
        {
            await ApiResults.WriteErrorAsync(context, ErrorCodes.PayloadTooLarge,
                "The JSON body is larger than the 65536 byte limit.",
                new { maxBytes = MaxJsonBodyBytes });
            return false;
        }

        // A chunked body declares no length, so cap it at the transport instead of trusting it.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = MaxJsonBodyBytes;

        var contentType = context.Request.ContentType;
        if (expectsJson && !IsJson(contentType))
        {
            await ApiResults.WriteErrorAsync(context, ErrorCodes.UnsupportedContentType,
                "Request bodies must be application/json.");
            return false;
        }

        return true;
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;

        var semicolon = contentType.IndexOf(';');
        var media = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fail closed. The only thing that gets past without a valid token is <c>GET /ping</c> with no
    /// <c>Authorization</c> header at all — and even <c>/ping</c> answers 401 for a token that is
    /// present and bad, because a client holding a dead token has to find out (§ 3).
    /// </summary>
    private static async Task<bool> AuthenticateAsync(
        HttpContext context, SecurityState state, bool isPing, DateTimeOffset now)
    {
        var header = context.Request.Headers.Authorization.ToString();

        // A token is accepted only here. Query strings are never consulted — they survive in proxy
        // logs, shell history and browser history — so `?token=…` simply leaves the request
        // unauthenticated (§ 3).
        if (string.IsNullOrWhiteSpace(header))
        {
            if (isPing) return true;

            await ApiResults.WriteErrorAsync(context, ErrorCodes.Unauthenticated,
                "This endpoint requires a bearer token in the Authorization header.");
            return false;
        }

        if (!TryReadBearer(header, out var presented))
        {
            await ApiResults.WriteErrorAsync(context, ErrorCodes.Unauthenticated,
                "The Authorization header must be a Bearer credential.");
            return false;
        }

        var check = state.Tokens.Check(presented, now);
        switch (check.Status)
        {
            case TokenStatus.Valid:
                context.Items[DeviceItemKey] = check.Device;
                return true;

            case TokenStatus.Revoked:
                await ApiResults.WriteErrorAsync(context, ErrorCodes.TokenRevoked,
                    "This device has been revoked.");
                return false;

            default:
                await ApiResults.WriteErrorAsync(context, ErrorCodes.InvalidToken,
                    "That bearer token is not valid.");
                return false;
        }
    }

    private static bool TryReadBearer(string header, out string token)
    {
        token = "";

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var value = header[scheme.Length..].Trim();
        if (value.Length == 0) return false;

        // One credential, not a list. "Bearer a, Bearer b" is an attempt to find the parser that
        // picks a different element than the one that was checked.
        if (value.Contains(',') || value.Contains(' ')) return false;

        token = value;
        return true;
    }

    /// <summary>
    /// Routing matches case-insensitively and tolerates one trailing slash, so the allow-list has to
    /// match the same set of strings the router does — otherwise a spelling that reaches an endpoint
    /// could take a different branch here. Anything this normalisation does not recognise is denied,
    /// which is the safe direction.
    /// </summary>
    private static string NormalisePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        return path.Length > 1 && path[^1] == '/' ? path[..^1] : path;
    }
}
