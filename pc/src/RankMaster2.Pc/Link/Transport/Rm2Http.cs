using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RankMaster2.Pc.Link.Wire;

namespace RankMaster2.Pc.Link.Transport;

internal enum UnreachableKind { Refused, Timeout, Cancelled, PinMismatch, Other }

/// <summary>What one HTTP exchange ended with. Never an exception out of <see cref="Rm2Http"/>.</summary>
internal abstract record Reply
{
    /// <summary>2xx. <paramref name="Body"/> is the raw bytes; the caller parses them into the type
    /// it expects and treats a parse failure the same as a <see cref="Refused"/> with
    /// <see cref="Codes.ClientMalformedResponse"/> — never a half-built snapshot, never an exception.</summary>
    public sealed record Ok(int Status, byte[] Body, string? RequestId) : Reply;

    /// <summary>A status arrived and it was not 2xx. <paramref name="Session"/> is the § 4 envelope's
    /// <c>error.session</c>, present iff the server sent one and it parsed.</summary>
    public sealed record Refused(
        int Status, string Code, string Message, string? RequestId,
        JsonElement? Details, Snapshot? Session) : Reply;

    /// <summary>No status arrived, or a body promised by a status could not be read. Nothing was
    /// learned about whether the request landed.</summary>
    public sealed record Unreachable(UnreachableKind Kind, string Detail) : Reply;
}

/// <summary>
/// The one class that owns an <see cref="HttpClient"/>, and there is one per link (§ 5.5).
/// </summary>
internal sealed class Rm2Http : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Action<string>? _log;

    /// <summary>Set by the link after every successful enrol/re-enrol, read on every send. This is
    /// what lets re-enrolment mid-session take effect on the very next send without rebuilding the
    /// client (§ 5.5).</summary>
    public string? BearerToken { get; set; }

    /// <param name="handler">The transport to send through — normally <see cref="PinnedHandler"/>'s
    /// output, optionally wrapped by the test harness's <c>wrap</c> hook (§ 4.3) so every fault script
    /// sits between the link and the real server.</param>
    public Rm2Http(string baseUrl, HttpMessageHandler handler, Action<string>? log)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _log = log;

        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    public async Task<Reply> Send(FrozenRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(request.Method, _baseUrl + request.Path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (request.Body.Length > 0 || request.Method == HttpMethod.Post)
        {
            httpRequest.Content = new ByteArrayContent(request.Body);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        if (request.Authenticate && BearerToken is { Length: > 0 } bearer)
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log(request, "cancelled", null, stopwatch, null);
            return new Reply.Unreachable(UnreachableKind.Cancelled, "The caller cancelled the request.");
        }
        catch (OperationCanceledException)
        {
            Log(request, "timeout", null, stopwatch, null);
            return new Reply.Unreachable(UnreachableKind.Timeout, $"No answer within {timeout.TotalSeconds:0.#} s.");
        }
        catch (HttpRequestException e)
        {
            var kind = PinnedHandler.IsPinMismatch(e) ? UnreachableKind.PinMismatch : UnreachableKind.Refused;
            Log(request, kind == UnreachableKind.PinMismatch ? "pin-mismatch" : "unreachable", null, stopwatch, null);
            return new Reply.Unreachable(kind, e.Message);
        }

        using (response)
        {
            var requestId = Header(response, "X-Request-Id");
            byte[] body;
            try
            {
                body = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log(request, "cancelled", (int)response.StatusCode, stopwatch, requestId);
                return new Reply.Unreachable(UnreachableKind.Cancelled, "The caller cancelled the request.");
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException)
            {
                // The status arrived and the body did not. Nothing was learned: this must be
                // retriable exactly like a lost request (§ 5.5).
                Log(request, "body-lost", (int)response.StatusCode, stopwatch, requestId);
                return new Reply.Unreachable(UnreachableKind.Other, "The response body could not be read: " + e.Message);
            }

            var status = (int)response.StatusCode;

            if (status is >= 200 and < 300)
            {
                Log(request, "ok", status, stopwatch, requestId);
                return new Reply.Ok(status, body, requestId);
            }

            var refused = ParseError(status, body, requestId);
            Log(request, refused.Code, status, stopwatch, requestId);
            return refused;
        }
    }

    private static Reply.Refused ParseError(int status, byte[] body, string? requestId)
    {
        try
        {
            if (body.Length == 0)
                return new Reply.Refused(status, Codes.ClientMalformedError,
                    $"The server answered {status} with no body.", requestId, null, null);

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
            {
                return new Reply.Refused(status, Codes.ClientMalformedError,
                    $"The server answered {status} without the error envelope this client expects.",
                    requestId, null, null);
            }

            // Read field by field (as OkHttpRm2Client.refuse does): an unparsable embedded session
            // must not cost the code and message too.
            var code = error.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString()! : Codes.ClientMalformedError;
            var message = error.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()! : $"The server answered {status} with no message.";
            var errRequestId = error.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String
                ? ridEl.GetString() : requestId;
            JsonElement? details = error.TryGetProperty("details", out var detEl) && detEl.ValueKind != JsonValueKind.Null
                ? detEl.Clone() : null;

            Snapshot? session = null;
            if (error.TryGetProperty("session", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.Object)
            {
                try { session = sessionEl.Deserialize(WireJsonContext.Default.Snapshot); }
                catch (JsonException) { session = null; }
            }

            return new Reply.Refused(status, code, message, errRequestId, details, session);
        }
        catch (JsonException)
        {
            return new Reply.Refused(status, Codes.ClientMalformedError,
                $"The server answered {status} with a body that is not valid JSON.", requestId, null, null);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private void Log(FrozenRequest request, string outcome, int? status, Stopwatch stopwatch, string? requestId)
    {
        if (_log is null) return;
        var statusText = status?.ToString() ?? "unreachable";
        _log($"{request.Method} {request.Path} → {statusText} {outcome} {stopwatch.ElapsedMilliseconds}ms {requestId}".TrimEnd());
    }

    public void Dispose() => _http.Dispose();
}
