using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// The API as the contract describes it, over a real HTTP surface.
///
/// Two rules shape this class. It never imports a type from the server, so a change to the server's
/// internal DTOs cannot silently change what the tests assert. And every call is bounded by a
/// timeout: three people are building this server concurrently, and a half-finished lock is far
/// likelier to hang a request than to answer it wrongly. A hung test says nothing; a timed-out one
/// names the endpoint.
/// </summary>
public sealed class Rm2Client(HttpClient http, string? token = null)
{
    public const string BasePath = "/api/v1";

    /// <summary>Generous enough for the 4000×3000 fixture, short enough that a deadlock is a failure.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public string? Token { get; set; } = token;

    public Rm2Client WithToken(string? newToken) => new(http, newToken);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- transport --------------------------------------------------------------------------

    public async Task<Rm2Response> SendAsync(HttpMethod method, string path, object? body = null,
                                             bool authenticate = true,
                                             Action<HttpRequestMessage>? customise = null)
    {
        var url = path.StartsWith("/api/", StringComparison.Ordinal) ? path : BasePath + path;
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            var text = body as string ?? JsonSerializer.Serialize(body, Json);
            request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        }

        if (authenticate && Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        customise?.Invoke(request);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead)
                                 .WaitAsync(CallTimeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"{method} {url} did not answer within {CallTimeout.TotalSeconds:0} s.\n" +
                "  The server is most likely blocked — a session semaphore taken and not released, or a " +
                "deadlock. SERVER_SPEC.md § 7.3 caps the wait at 5 s and requires 503 session_busy after it, " +
                "so a request that hangs is itself a contract failure.");
        }
        catch (TaskCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"{method} {url} was cancelled before it answered (the HTTP client timed out).\n" +
                "  See SERVER_SPEC.md § 7.3: the session lock has a 5 s cap and must answer 503 session_busy.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new Rm2Response
        {
            Method = method.Method,
            Url = url,
            Status = response.StatusCode,
            Raw = response,
            Body = bytes
        };
    }

    public Task<Rm2Response> GetAsync(string path, bool authenticate = true,
                                      Action<HttpRequestMessage>? customise = null) =>
        SendAsync(HttpMethod.Get, path, null, authenticate, customise);

    public Task<Rm2Response> HeadAsync(string path, bool authenticate = true) =>
        SendAsync(HttpMethod.Head, path, null, authenticate);

    // ---- system -----------------------------------------------------------------------------

    public Task<Rm2Response> PingAsync(bool authenticate = true) => GetAsync("/ping", authenticate);

    public Task<Rm2Response> PairAsync(string code, string? deviceName = null) =>
        SendAsync(HttpMethod.Post, "/pair", new { code, deviceName }, authenticate: false);

    public Task<Rm2Response> RevokeAsync(string deviceId) =>
        SendAsync(HttpMethod.Delete, $"/pair/{Uri.EscapeDataString(deviceId)}");

    public Task<Rm2Response> RootsAsync() => GetAsync("/libraries/roots");

    public Task<Rm2Response> BrowseAsync(string path, bool? counts = null)
    {
        var query = $"/libraries/browse?path={Uri.EscapeDataString(path)}";
        if (counts is { } value) query += $"&counts={(value ? "true" : "false")}";
        return GetAsync(query);
    }

    // ---- session ----------------------------------------------------------------------------

    public Task<Rm2Response> OpenSessionAsync(string folder) =>
        SendAsync(HttpMethod.Post, "/session", new { folder });

    public Task<Rm2Response> GetSessionAsync() => GetAsync("/session");

    public Task<Rm2Response> GetPairAsync() => GetAsync("/session/pair");

    public Task<Rm2Response> CloseSessionAsync() => SendAsync(HttpMethod.Delete, "/session");

    public Task<Rm2Response> SaveAsync() => SendAsync(HttpMethod.Post, "/session/save", new { });

    public Task<Rm2Response> VoteAsync(string pairToken, string winner, string? clientRequestId = null) =>
        SendAsync(HttpMethod.Post, "/session/vote", new { pairToken, winner, clientRequestId });

    public Task<Rm2Response> SkipAsync(string pairToken, string? clientRequestId = null) =>
        SendAsync(HttpMethod.Post, "/session/skip", new { pairToken, clientRequestId });

    public Task<Rm2Response> DiscardAsync(string pairToken, string side, string? clientRequestId = null) =>
        SendAsync(HttpMethod.Post, "/session/discard", new { pairToken, side, clientRequestId });

    public Task<Rm2Response> SpecialAsync(string pairToken, string side, string? clientRequestId = null) =>
        SendAsync(HttpMethod.Post, "/session/special", new { pairToken, side, clientRequestId });

    public Task<Rm2Response> UndoAsync(string? clientRequestId = null) =>
        SendAsync(HttpMethod.Post, "/session/undo", new { clientRequestId });

    // ---- rename (SERVER_SPEC.md § 10.16) -----------------------------------------------------

    public Task<Rm2Response> StartRenameAsync() =>
        SendAsync(HttpMethod.Post, "/session/rename", new { });

    public Task<Rm2Response> GetRenameAsync() => GetAsync("/session/rename");

    public Task<Rm2Response> CancelRenameAsync() =>
        SendAsync(HttpMethod.Post, "/session/rename/cancel", new { });

    // ---- media ------------------------------------------------------------------------------

    /// <summary>
    /// RFC 3986 path-segment encoding of a filename, which is what SERVER_SPEC.md § 11.1 requires
    /// of a client: a space becomes %20 and never '+', and every byte ≥ 0x80 is encoded.
    /// <see cref="Uri.EscapeDataString"/> leaves exactly the unreserved set alone, which is the
    /// stricter end of what § 11.1 permits.
    /// </summary>
    public static string EncodeId(string id) => Uri.EscapeDataString(id);

    public Task<Rm2Response> MetaAsync(string id) => GetAsync($"/media/{EncodeId(id)}/meta");

    public Task<Rm2Response> StillAsync(string id, int? width = null, string? format = null,
                                        string? version = null, string? ifNoneMatch = null,
                                        string? accept = null)
    {
        var query = new List<string>();
        if (width is { } w) query.Add($"w={w}");
        if (format is not null) query.Add($"format={Uri.EscapeDataString(format)}");
        if (version is not null) query.Add($"v={Uri.EscapeDataString(version)}");

        var path = $"/media/{EncodeId(id)}/still" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return GetAsync(path, customise: request => ApplyMediaHeaders(request, ifNoneMatch, accept));
    }

    public Task<Rm2Response> ThumbAsync(string id, string? format = null, int? width = null,
                                        string? ifNoneMatch = null, string? accept = null)
    {
        var query = new List<string>();
        if (format is not null) query.Add($"format={Uri.EscapeDataString(format)}");
        if (width is { } w) query.Add($"w={w}");

        var path = $"/media/{EncodeId(id)}/thumb" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return GetAsync(path, customise: request => ApplyMediaHeaders(request, ifNoneMatch, accept));
    }

    public Task<Rm2Response> VideoAsync(string id, string? range = null, string? ifRange = null,
                                        string? ifNoneMatch = null)
        => GetAsync($"/media/{EncodeId(id)}/video", customise: request =>
        {
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            if (ifRange is not null) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        });

    /// <summary>Fetch a URL the server itself put in a snapshot's <c>links</c>, verbatim (§ 9.3).</summary>
    public Task<Rm2Response> FollowAsync(string link) => GetAsync(link);

    private static void ApplyMediaHeaders(HttpRequestMessage request, string? ifNoneMatch, string? accept)
    {
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
    }
}
