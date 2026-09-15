using System.Net.Http.Headers;
using System.Text.Json;
using RankMaster2.Audit.Conformance.Audit;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Conformance;

/// <summary>SERVER_SPEC.md § 3 — authentication, and § 4's rules about what an auth failure may say.</summary>
public sealed class AuthConformanceTests(Rm2Server server) : AuditTestBase(server)
{

    [Theory]
    [InlineData("/session")]
    [InlineData("/session/pair")]
    [InlineData("/libraries/roots")]
    [InlineData("/libraries/browse?path=/tmp")]
    [InlineData("/media/alpha.jpg/meta")]
    [InlineData("/media/alpha.jpg/still")]
    [InlineData("/media/alpha.jpg/thumb")]
    [InlineData("/media/clip.avi/video")]
    public async Task NoAuthorizationHeaderIsUnauthenticated(string path)
    {
        var response = await Anonymous.GetAsync(path, authenticate: false);
        response.Error("unauthenticated",
            $"SERVER_SPEC.md § 3: \"A missing or malformed Authorization header → 401 unauthenticated\" ({path})");
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Token abcdef")]
    [InlineData("bearer")]
    [InlineData("Bearer")]
    public async Task MalformedAuthorizationHeaderIsUnauthenticated(string header)
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, "/session", authenticate: false,
            customise: request => request.Headers.TryAddWithoutValidation("Authorization", header));

        response.Error("unauthenticated",
            $"SERVER_SPEC.md § 3: an Authorization header that is not `Bearer <token>` is " +
            $"401 unauthenticated, not invalid_token (sent '{header}')");
    }

    [Fact]
    public async Task WellFormedButUnknownTokenIsInvalidToken()
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, "/session", authenticate: false,
            customise: request => request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "rm2_this_token_was_never_issued_000000"));

        response.Error("invalid_token",
            "SERVER_SPEC.md § 3: \"A well-formed but unknown/expired token → 401 invalid_token\" — " +
            "distinct from unauthenticated, so a client can tell a missing credential from a bad one");
    }

    [Fact]
    public async Task RevokedTokenIsTokenRevoked()
    {
        var spare = Server.SpareDevice;
        Assert.True(spare is not null,
            "No spare device could be paired, so revocation cannot be exercised at all. " +
            "SERVER_SPEC.md § 3 requires 401 token_revoked for a revoked device, and an audit that " +
            "silently skipped this clause would look like one that proved it.");

        var client = await ClientAsync();
        var revoke = await client.RevokeAsync(spare!.DeviceId);
        revoke.Status(204, "SERVER_SPEC.md § 10.12: DELETE /pair/{deviceId} answers 204 on success");

        var response = await Anonymous.SendAsync(HttpMethod.Get, "/session", authenticate: false,
            customise: request => request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", spare.Token));

        response.Error("token_revoked",
            "SERVER_SPEC.md § 3 and § 10.12: \"the revoked device's next call gets 401 token_revoked\" — " +
            "a revoked device must not be told merely that its token is unknown");
    }

    [Fact]
    public async Task TokenInTheQueryStringIsIgnored()
    {
        var credentials = await Server.AuthenticateAsync();
        Assert.True(credentials.Mode == AuthMode.Bearer,
            "This clause needs a real token to smuggle into a query string.\n" + credentials.Diagnostic);

        var response = await Anonymous.GetAsync(
            $"/session?access_token={Uri.EscapeDataString(credentials.Token!)}", authenticate: false);

        response.Error("unauthenticated",
            "SERVER_SPEC.md § 3: \"A token in a query string MUST be ignored, and the request MUST be " +
            "treated as unauthenticated\" — otherwise tokens land in logs and proxy history");
    }

    [Theory]
    [InlineData("token")]
    [InlineData("bearer")]
    [InlineData("authorization")]
    public async Task OtherQueryStringTokenSpellingsAreAlsoIgnored(string parameter)
    {
        var credentials = await Server.AuthenticateAsync();
        Assert.True(credentials.Mode == AuthMode.Bearer,
            "This clause needs a real token to smuggle into a query string.\n" + credentials.Diagnostic);

        var response = await Anonymous.GetAsync(
            $"/session?{parameter}={Uri.EscapeDataString(credentials.Token!)}", authenticate: false);

        response.Error("unauthenticated",
            $"SERVER_SPEC.md § 3: a token in ?{parameter}= is ignored and the request is unauthenticated");
    }

    // ---- § 3, /ping ------------------------------------------------------------------------------

    [Fact]
    public async Task PingWithoutATokenReturnsThePublicSubset()
    {
        var response = await Anonymous.PingAsync(authenticate: false);
        var body = response.Status(200,
            "SERVER_SPEC.md § 3: \"GET /ping without a token MUST succeed and return the public subset\"")
            .RequireJson("§ 14");

        Assert.False(body.GetProperty("authenticated").GetBoolean(),
            "SERVER_SPEC.md § 14: `authenticated` is false in the public subset.");
        Assert.True(body.GetProperty("session").ValueKind == JsonValueKind.Null,
            "SERVER_SPEC.md § 14: \"`session` is null in the public subset\"; got " +
            $"{body.GetProperty("session")}.");
    }

    [Fact]
    public async Task PingWithAnInvalidTokenIs401NotThePublicSubset()
    {
        var response = await Anonymous.SendAsync(HttpMethod.Get, "/ping", authenticate: false,
            customise: request => request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "rm2_not_a_real_token_at_all"));

        response.Error("invalid_token",
            "SERVER_SPEC.md § 3: \"With an *invalid* token it MUST return 401, not the public subset — " +
            "a client with a bad token must learn that it is bad\"");
    }

    [Fact]
    public async Task PingWithAValidTokenReturnsTheFullBody()
    {
        var credentials = await Server.AuthenticateAsync();
        Assert.True(credentials.Mode == AuthMode.Bearer,
            "This clause needs a real token.\n" + credentials.Diagnostic);

        var client = await ClientAsync();
        var body = (await client.PingAsync())
            .Status(200, "SERVER_SPEC.md § 10.13")
            .RequireJson("§ 14");

        Assert.True(body.GetProperty("authenticated").GetBoolean(),
            "SERVER_SPEC.md § 14: `authenticated` is true once a valid token is presented.");
        Assert.True(body.GetProperty("session").ValueKind == JsonValueKind.Object,
            "SERVER_SPEC.md § 14: authenticated, `session` is an object — \"a cheap way to ask 'is a " +
            $"session open' without a 404\". Got {body.GetProperty("session").ValueKind}.");
    }

    // ---- § 4, what an auth failure may not carry -------------------------------------------------

    [Fact]
    public async Task A401NeverCarriesTheSessionSnapshot()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        (await client.OpenSessionAsync(folder.Path))
            .Status(201, "SERVER_SPEC.md § 10.1");

        var probes = new[]
        {
            await Anonymous.GetSessionAsync(),
            await Anonymous.SendAsync(HttpMethod.Post, "/session/vote",
                new { pairToken = "whatever", winner = "left" }, authenticate: false),
            await Anonymous.SendAsync(HttpMethod.Post, "/session/undo", new { }, authenticate: false),
            await Anonymous.SendAsync(HttpMethod.Delete, "/session", authenticate: false),
            await Anonymous.GetAsync($"/media/{MediaFixtures.PlainJpeg}/meta", authenticate: false)
        };

        foreach (var probe in probes)
        {
            var error = probe.AnyError("SERVER_SPEC.md § 4");
            Assert.True(!error.TryGetProperty("session", out var session) ||
                        session.ValueKind == JsonValueKind.Null,
                "SERVER_SPEC.md § 4: \"error.session MUST be omitted on 401 and 403. The snapshot carries " +
                "the open folder's absolute path and its contents. Attaching it to an authentication failure " +
                "would hand exactly what the token exists to protect to a caller who has just failed to " +
                $"present one.\" {probe.Method} {probe.Url} → {probe.StatusCode} leaked it.\n{probe.Describe()}");
        }
    }

    [Fact]
    public async Task A401MessageDoesNotNameTheOpenFolder()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var probe = await Anonymous.GetSessionAsync();
        Assert.DoesNotContain(folder.Path, probe.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- § 10.12, revocation ---------------------------------------------------------------------

    [Fact]
    public async Task RevokingAnUnknownDeviceIsNotFound()
    {
        var client = await ClientAsync();
        var response = await client.RevokeAsync("dev_no_such_device");

        response.Error("not_found",
            "SERVER_SPEC.md § 10.12: \"404 not_found for an unknown deviceId\"");
    }

    [Fact]
    public async Task RevocationDoesNotCloseAnOpenSession()
    {
        var spare = Server.SpareDevice;
        Assert.True(spare is not null, "No spare device could be paired, so revocation cannot be exercised.");

        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1");

        var revoke = await client.RevokeAsync(spare!.DeviceId);
        Assert.True(revoke.StatusCode is 204 or 404,
            $"SERVER_SPEC.md § 10.12: revocation is 204, or 404 if the device is already gone. Got " +
            $"{revoke.StatusCode}.\n{revoke.Describe()}");

        var after = (await client.GetSessionAsync())
            .RequireSnapshot(200,
                "SERVER_SPEC.md § 10.12: \"Revocation does not close an open session — DELETE /session " +
                "is the only thing that does\"");

        Assert.Equal(opened.SessionId, after.SessionId);
    }

    // ---- § 10.11, pairing refusals ---------------------------------------------------------------

    [Fact]
    public async Task PairingWithoutACodeIsMissingField()
    {
        var response = await Anonymous.SendAsync(HttpMethod.Post, "/pair", "{}", authenticate: false);

        var code = response.ErrorCodeOrThrow("SERVER_SPEC.md § 4");
        Assert.True(code is "missing_field" or "invalid_request",
            "openapi.yaml makes `code` required on PairRequest, so SERVER_SPEC.md § 5.2 answers " +
            $"400 missing_field. Got '{code}' at HTTP {response.StatusCode}.\n{response.Describe()}");
        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task PairEndpointNeedsNoToken()
    {
        // Not a successful pairing — merely proof that the endpoint does not demand a bearer token
        // first, which § 3 makes explicit: "POST /pair | none".
        var response = await Anonymous.PairAsync("000000", "audit probe");

        Assert.True(response.StatusCode != 401 || response.ErrorCode == "invalid_pairing_code",
            "SERVER_SPEC.md § 3: POST /pair takes no bearer token — \"a valid one-time pairing code is the " +
            $"credential\". A wrong code is invalid_pairing_code, never unauthenticated. Got " +
            $"'{response.ErrorCode}'.\n{response.Describe()}");
    }
}
