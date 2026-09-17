using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace RankMaster2.Cli;

/// <summary>One exchange with the server, kept whole so the journal can describe it.</summary>
public sealed class Reply
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required int Status { get; init; }
    public required byte[] Body { get; init; }
    public required HttpResponseMessage Raw { get; init; }

    public string? ContentType => Raw.Content.Headers.ContentType?.ToString();
    public string Text => Encoding.UTF8.GetString(Body);

    private JsonElement? _json;
    private bool _parsed;

    public JsonElement? Json
    {
        get
        {
            if (_parsed) return _json;
            _parsed = true;
            if (Body.Length == 0) return _json = null;
            try { _json = JsonDocument.Parse(Body).RootElement.Clone(); }
            catch (JsonException) { _json = null; }
            return _json;
        }
    }

    public string? Header(string name)
    {
        if (Raw.Headers.TryGetValues(name, out var values)) return string.Join(", ", values);
        if (Raw.Content.Headers.TryGetValues(name, out var content)) return string.Join(", ", content);
        return null;
    }

    /// <summary>The `error.code` of the envelope (SERVER_SPEC.md § 4), or null if this is not one.</summary>
    public string? ErrorCode =>
        Json is { } json && json.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;

    public string? ErrorMessage =>
        Json is { } json && json.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;

    public JsonElement? ErrorDetails =>
        Json is { } json && json.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("details", out var details) &&
        details.ValueKind != JsonValueKind.Null
            ? details
            : null;

    /// <summary>A one-line description for the journal: the error code, or a short shape summary.</summary>
    public string Summarise()
    {
        if (ErrorCode is { } code)
            return $"{code} — {Journal.Trim(ErrorMessage ?? "", 90)}";

        if (Body.Length == 0) return "(no body)";

        // Journal.Summarise, not Journal.Trim: a body carrying a device token or a pairing code must
        // not appear here half-printed above the full copy the command prints itself (K3).
        if (ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return Journal.Summarise(Text.ReplaceLineEndings(" "), 110);

        return $"{Body.Length} bytes of {ContentType ?? "unknown type"}";
    }
}

/// <summary>
/// The HTTP surface of SERVER_SPEC.md, as a client speaks it.
///
/// Two things here are contract, not convenience. The bearer token goes in the Authorization header
/// and nowhere else (§ 3 — a token in a query string is ignored, so that a token never lands in a
/// log or a proxy history). And the certificate is pinned by SHA-256 of its DER encoding (§ 14),
/// which is the only reason a self-signed certificate is security rather than decoration.
/// </summary>
public sealed class Rm2Api : IDisposable
{
    private readonly HttpClient _http;
    private readonly Journal _journal;

    public string BaseUrl { get; }
    public string? Token { get; set; }

    /// <summary>The fingerprint the server actually presented, learned during the handshake.</summary>
    public string? ObservedFingerprint { get; private set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Rm2Api(string baseUrl, Journal journal, string? pinnedFingerprint, bool insecure, TimeSpan timeout)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _journal = journal;

        var handler = new HttpClientHandler();

        if (BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null) return false;

                var fingerprint = "sha256:" + Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
                ObservedFingerprint = fingerprint;

                if (pinnedFingerprint is { Length: > 0 })
                    return string.Equals(fingerprint, Normalise(pinnedFingerprint), StringComparison.OrdinalIgnoreCase);

                if (insecure) return true;

                // Trust on first use: the certificate is self-signed by design, so chain errors are
                // expected and meaningless. The fingerprint is printed so it can be pinned next time.
                return errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors
                                                      or SslPolicyErrors.RemoteCertificateNameMismatch;
            };
        }

        _http = new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// Turn a path into a URL. The <c>links</c> in a snapshot are already rooted at the API base
    /// ("/api/v1/media/…", § 9.3), while the paths in this class are relative to it — so a link is
    /// resolved against the origin and everything else against the base, and using a link verbatim
    /// does not end up asking for /api/v1/api/v1/…
    /// </summary>
    private string Resolve(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;

        if (!path.StartsWith('/')) path = "/" + path;

        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            var origin = new Uri(BaseUrl).GetLeftPart(UriPartial.Authority);
            return origin + path;
        }

        return BaseUrl + path;
    }

    private static string Normalise(string fingerprint)
    {
        var value = fingerprint.Trim().ToLowerInvariant().Replace(":", "", StringComparison.Ordinal);
        if (value.StartsWith("sha256", StringComparison.Ordinal)) value = value["sha256".Length..];
        return "sha256:" + value;
    }

    // ---- transport ----------------------------------------------------------------------------

    public async Task<Reply> SendAsync(HttpMethod method, string path, object? body = null,
                                       bool authenticate = true,
                                       Action<HttpRequestMessage>? customise = null,
                                       bool quiet = false)
    {
        var url = Resolve(path);
        using var request = new HttpRequestMessage(method, url);

        string? serialised = null;
        if (body is not null)
        {
            serialised = body as string ?? JsonSerializer.Serialize(body, Json);
            request.Content = new StringContent(serialised, Encoding.UTF8, "application/json");
        }

        if (authenticate && Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        customise?.Invoke(request);

        if (!quiet) _journal.Request(method.Method, url, serialised);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        }
        catch (TaskCanceledException)
        {
            throw new Rm2Unreachable(
                $"{method} {url} did not answer within {_http.Timeout.TotalSeconds:0} s.\n" +
                "SERVER_SPEC.md § 7.3 caps the session lock wait at 5 s and requires 503 session_busy " +
                "after it, so a request that simply hangs is itself a contract failure.");
        }
        catch (HttpRequestException e)
        {
            throw new Rm2Unreachable(
                $"{method} {url} could not be reached: {e.Message}\n" +
                (e.InnerException is not null ? e.InnerException.Message + "\n" : "") +
                "Check --base, and whether the certificate matches --pin. Use --insecure to accept any " +
                "certificate while you find out.");
        }

        var reply = new Reply
        {
            Method = method.Method,
            Url = url,
            Status = (int)response.StatusCode,
            Body = await response.Content.ReadAsByteArrayAsync(),
            Raw = response
        };

        if (!quiet) _journal.Response(reply.Status, reply.Summarise());
        return reply;
    }

    public Task<Reply> GetAsync(string path, bool authenticate = true,
                                Action<HttpRequestMessage>? customise = null, bool quiet = false) =>
        SendAsync(HttpMethod.Get, path, null, authenticate, customise, quiet);

    // ---- endpoints ----------------------------------------------------------------------------

    public Task<Reply> PingAsync(bool authenticate = true) => GetAsync("/ping", authenticate);

    public Task<Reply> PairAsync(string code, string? deviceName) =>
        SendAsync(HttpMethod.Post, "/pair", new { code, deviceName }, authenticate: false);

    public Task<Reply> OpenSessionAsync(string folder) =>
        SendAsync(HttpMethod.Post, "/session", new { folder });

    public Task<Reply> GetSessionAsync() => GetAsync("/session");
    public Task<Reply> GetPairAsync() => GetAsync("/session/pair");
    public Task<Reply> CloseSessionAsync() => SendAsync(HttpMethod.Delete, "/session");
    public Task<Reply> SaveAsync() => SendAsync(HttpMethod.Post, "/session/save", new { });

    public Task<Reply> VoteAsync(string pairToken, string winner, string? clientRequestId) =>
        SendAsync(HttpMethod.Post, "/session/vote", new { pairToken, winner, clientRequestId });

    public Task<Reply> SkipAsync(string pairToken, string? clientRequestId) =>
        SendAsync(HttpMethod.Post, "/session/skip", new { pairToken, clientRequestId });

    public Task<Reply> DiscardAsync(string pairToken, string side, string? clientRequestId) =>
        SendAsync(HttpMethod.Post, "/session/discard", new { pairToken, side, clientRequestId });

    public Task<Reply> SpecialAsync(string pairToken, string side, string? clientRequestId) =>
        SendAsync(HttpMethod.Post, "/session/special", new { pairToken, side, clientRequestId });

    public Task<Reply> UndoAsync(string? clientRequestId) =>
        SendAsync(HttpMethod.Post, "/session/undo", new { clientRequestId });

    // ---- rename (SERVER_SPEC.md § 10.16) -------------------------------------------------------

    public Task<Reply> StartRenameAsync(string? clientRequestId = null) =>
        SendAsync(HttpMethod.Post, "/session/rename", new { clientRequestId });

    public Task<Reply> GetRenameAsync() => GetAsync("/session/rename");

    public Task<Reply> CancelRenameAsync() =>
        SendAsync(HttpMethod.Post, "/session/rename/cancel", new { });

    public Task<Reply> RootsAsync() => GetAsync("/libraries/roots");

    public Task<Reply> BrowseAsync(string path, bool? counts = null)
    {
        var query = $"/libraries/browse?path={Uri.EscapeDataString(path)}";
        if (counts is { } value) query += $"&counts={(value ? "true" : "false")}";
        return GetAsync(query);
    }

    /// <summary>
    /// RFC 3986 path-segment encoding, which is what SERVER_SPEC.md § 11.1 asks of a client: a space
    /// is %20 and never '+', and every byte above 0x7F is encoded.
    /// </summary>
    public static string EncodeId(string id) => Uri.EscapeDataString(id);

    public Task<Reply> MetaAsync(string id) => GetAsync($"/media/{EncodeId(id)}/meta");

    public Task<Reply> StillAsync(string id, int? width = null, string? format = null, string? accept = null,
                                  string? ifNoneMatch = null)
    {
        var query = new List<string>();
        if (width is { } w) query.Add($"w={w}");
        if (format is not null) query.Add($"format={Uri.EscapeDataString(format)}");

        var path = $"/media/{EncodeId(id)}/still" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return GetAsync(path, customise: request =>
        {
            if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
            if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        });
    }

    public Task<Reply> ThumbAsync(string id) => GetAsync($"/media/{EncodeId(id)}/thumb");

    public Task<Reply> VideoAsync(string id, string? range = null) =>
        GetAsync($"/media/{EncodeId(id)}/video", customise: request =>
        {
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
        });

    /// <summary>Fetch a link the server itself put in a snapshot (§ 9.3), verbatim.</summary>
    public Task<Reply> FollowAsync(string link) => GetAsync(link);

    public Task<Reply> HeadAsync(string path) => SendAsync(HttpMethod.Head, path);

    public void Dispose() => _http.Dispose();
}

/// <summary>The server could not be reached or did not answer. Not a contract failure — a setup one.</summary>
public sealed class Rm2Unreachable(string message) : Exception(message);
