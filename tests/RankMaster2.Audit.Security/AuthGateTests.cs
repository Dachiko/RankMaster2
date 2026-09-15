using System.Net.Http.Headers;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>
/// SERVER_SPEC.md § 3. The bearer token is the only barrier between the LAN and the whole
/// filesystem, so the interesting question is not "does /session need a token" but "is there any
/// spelling of any request that gets past the gate".
/// </summary>
public sealed class AuthGateTests(Rm2Server server) : AuditTestBase(server)
{
    /// <summary>Every route the server maps, with the method that reaches it.</summary>
    public static TheoryData<string, string> ProtectedRoutes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, path) in new[]
        {
            ("GET", "/api/v1/session"),
            ("POST", "/api/v1/session"),
            ("DELETE", "/api/v1/session"),
            ("GET", "/api/v1/session/pair"),
            ("POST", "/api/v1/session/save"),
            ("POST", "/api/v1/session/vote"),
            ("POST", "/api/v1/session/skip"),
            ("POST", "/api/v1/session/discard"),
            ("POST", "/api/v1/session/special"),
            ("POST", "/api/v1/session/undo"),
            ("DELETE", "/api/v1/pair/dev_0000000000000000"),
            ("GET", "/api/v1/libraries/roots"),
            ("GET", "/api/v1/libraries/browse?path=%2F"),
            ("GET", "/api/v1/media/x.jpg/meta"),
            ("GET", "/api/v1/media/x.jpg/still"),
            ("GET", "/api/v1/media/x.jpg/thumb"),
            ("GET", "/api/v1/media/x.mp4/video"),
            ("HEAD", "/api/v1/media/x.jpg/meta"),
        })
        {
            data.Add(method, path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task NoToken_is_401_unauthenticated(string method, string path)
    {
        var response = await Anonymous.SendAsync(new HttpMethod(method), path, authenticate: false);

        response.ShouldHaveStatus(401,
            $"SERVER_SPEC.md § 3: {method} {path} carries no Authorization header, so it must be " +
            "401 unauthenticated. Nothing but GET /ping and POST /pair answers without a token");
        Assert.Equal("unauthenticated", response.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Token_in_the_query_string_is_ignored(string method, string path)
    {
        var credentials = await Server.AuthenticateAsync();
        Assert.Equal(AuthMode.Bearer, credentials.Mode);

        var separator = path.Contains('?') ? '&' : '?';
        var withToken = $"{path}{separator}token={Uri.EscapeDataString(credentials.Token!)}" +
                        $"&access_token={Uri.EscapeDataString(credentials.Token!)}";

        var response = await Anonymous.SendAsync(new HttpMethod(method), withToken, authenticate: false);

        response.ShouldHaveStatus(401,
            "SERVER_SPEC.md § 3: a token in a query string MUST be ignored and the request treated " +
            "as unauthenticated");
    }

    public static TheoryData<string, string> MalformedHeaders() => new()
    {
        { "empty scheme only", "Bearer" },
        { "scheme with no credential", "Bearer " },
        { "wrong scheme", "Basic cm9vdDpyb290" },
        { "no scheme at all", "rm2_abcdefghijklmnop.qrstuvwx" },
        { "two credentials", "Bearer aaa, Bearer bbb" },
        { "tab separator", "Bearer\trm2_aaaa.bbbb" },
        { "leading whitespace", "  Bearer rm2_aaaa.bbbb" },
        { "embedded space", "Bearer rm2_aaaa .bbbb" },
        { "very long", "Bearer rm2_" + new string('A', 8000) },
    };

    [Theory]
    [MemberData(nameof(MalformedHeaders))]
    public async Task Malformed_authorization_header_is_401(string label, string header)
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, "/api/v1/session", authenticate: false,
            customise: request => request.Headers.TryAddWithoutValidation("Authorization", header));

        response.ShouldHaveStatus(401,
            $"SERVER_SPEC.md § 3 ({label}): a missing or malformed Authorization header is 401");
        Assert.Contains(response.ErrorCode, new[] { "unauthenticated", "invalid_token" });
    }

    [Fact]
    public async Task A_second_authorization_header_cannot_smuggle_a_good_token_past_a_bad_one()
    {
        var credentials = await Server.AuthenticateAsync();
        Assert.Equal(AuthMode.Bearer, credentials.Mode);

        var response = await Anonymous.SendAsync(HttpMethod.Get, "/api/v1/ping", authenticate: false,
            customise: request =>
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer rm2_notatoken.notasecret");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credentials.Token);
            });

        response.ShouldHaveStatus(401,
            "SERVER_SPEC.md § 3: one credential is presented, not a list. Two Authorization headers " +
            "means one layer could pick a different element than the one that was checked");
    }

    [Fact]
    public async Task An_unknown_token_is_401_invalid_token()
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, "/api/v1/session", authenticate: false,
            customise: request => request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "rm2_AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));

        response.ShouldHaveStatus(401, "SERVER_SPEC.md § 3: a well-formed but unknown token is 401 invalid_token");
        Assert.Equal("invalid_token", response.ErrorCode);
    }

    [Fact]
    public async Task Ping_with_a_bad_token_is_401_and_not_the_public_subset()
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, "/api/v1/ping", authenticate: false,
            customise: request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer rm2_bad.bad"));

        response.ShouldHaveStatus(401,
            "SERVER_SPEC.md § 3: /ping with an INVALID token must be 401, not the public subset — " +
            "a client with a bad token must learn that it is bad");
    }

    [Fact]
    public async Task A_revoked_device_gets_401_token_revoked_everywhere()
    {
        var spare = Server.SpareDevice;
        Assert.NotNull(spare);

        var client = await ClientAsync();
        var revoked = Anonymous.WithToken(spare!.Token);

        // Prove the spare works before it is revoked.
        (await revoked.PingAsync()).ShouldHaveStatus(200, "the spare device is paired and can call /ping");

        (await client.RevokeAsync(spare.DeviceId))
            .ShouldHaveStatus(204, "SERVER_SPEC.md § 10.12: DELETE /pair/{deviceId} is 204");

        foreach (var (method, path) in new[]
        {
            ("GET", "/api/v1/session"),
            ("GET", "/api/v1/ping"),
            ("GET", "/api/v1/libraries/roots"),
            ("GET", "/api/v1/media/x.jpg/meta"),
        })
        {
            var response = await revoked.SendAsync(new HttpMethod(method), path);
            response.ShouldHaveStatus(401,
                $"SERVER_SPEC.md § 3: {method} {path} with a revoked device's token is 401");
            Assert.Equal("token_revoked", response.ErrorCode);
        }
    }

    /// <summary>
    /// The gate is an allow-list keyed on the request path. Routing matches case-insensitively and
    /// tolerates a trailing slash, so any spelling that reaches a route must take the same branch
    /// in the gate that the route itself would.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/SESSION")]
    [InlineData("/api/v1/session/")]
    [InlineData("/api/v1/Libraries/Roots")]
    [InlineData("/api/v1/libraries/roots/")]
    [InlineData("/api/v1/session/../session")]
    [InlineData("/api/v1/ping/../session")]
    [InlineData("/api/v1/./session")]
    [InlineData("/api/v1//session")]
    [InlineData("/api/v1/%73ession")]
    [InlineData("/api/v1/ping%2f..%2fsession")]
    [InlineData("/api/v1/session%20")]
    [InlineData("/api/v1/session;/")]
    public async Task No_spelling_of_a_protected_path_answers_without_a_token(string path)
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, path, authenticate: false);

        Assert.True(response.StatusCode is 401 or 404 or 400,
            $"SERVER_SPEC.md § 3: an unauthenticated GET {path} must not be served. " +
            $"It answered {response.StatusCode}: {response.Text}");
    }

    /// <summary>
    /// The gate's allow-list tests the path only, so it lets any method through on /ping. Routing
    /// then answers 405, which the transport turns into the envelope. Not a hole on its own — there
    /// is no non-GET handler on /ping — but it is the shape of one, so it is pinned here.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    public async Task A_non_GET_method_on_ping_reveals_nothing(string method)
    {
        var response = await Anonymous.SendAsync(new HttpMethod(method), "/api/v1/ping", authenticate: false);

        Assert.True(response.StatusCode is 404 or 405 or 401,
            $"{method} /api/v1/ping without a token answered {response.StatusCode}: {response.Text}");
        Assert.DoesNotContain("certificateFingerprint", response.Text, StringComparison.Ordinal);
    }
}
