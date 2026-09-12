using System.Net.Http.Headers;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// SERVER_SPEC.md § 3. The bearer token "is the only barrier between the LAN and the whole
/// filesystem", so these are not box-ticking tests: `/libraries/browse` walks any path the caller
/// names, and it sits behind this check and nothing else.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class AuthenticationTests(Rm2Server server) : SessionTestBase(server)
{
    /// <summary>Everything except `POST /pair` and `GET /ping` needs a token (§ 3).</summary>
    public static TheoryData<string, string> ProtectedEndpoints => new()
    {
        { "GET", "/session" },
        { "GET", "/session/pair" },
        { "POST", "/session" },
        { "DELETE", "/session" },
        { "POST", "/session/save" },
        { "POST", "/session/vote" },
        { "POST", "/session/skip" },
        { "POST", "/session/discard" },
        { "POST", "/session/special" },
        { "POST", "/session/undo" },
        { "GET", "/libraries/roots" },
        { "GET", "/libraries/browse?path=/tmp" },
        { "GET", "/media/alpha.jpg/meta" },
        { "GET", "/media/alpha.jpg/still" },
        { "GET", "/media/alpha.jpg/thumb" },
        { "GET", "/media/alpha.jpg/video" },
        { "DELETE", "/pair/some-device" },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task No_authorization_header_is_unauthenticated(string method, string path)
    {
        var body = method is "POST" ? new { } : null;
        var response = await Anonymous.SendAsync(new HttpMethod(method), path, body, authenticate: false);

        response.ShouldBeError("unauthenticated",
            $"SERVER_SPEC.md § 3: {method} {path} with no Authorization header is 401 unauthenticated. " +
            "This check is the only thing standing between the LAN and the filesystem.");
    }

    [Fact]
    public async Task A_non_bearer_scheme_is_unauthenticated()
    {
        var response = await Anonymous.GetAsync("/session", authenticate: false, customise: request =>
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz"));

        response.ShouldBeError("unauthenticated",
            "SERVER_SPEC.md § 3: an Authorization header that is not Bearer is 401 unauthenticated.");
    }

    [Fact]
    public async Task A_well_formed_but_unknown_token_is_an_invalid_token()
    {
        var response = await Anonymous.WithToken("rm2_this_token_was_never_issued").GetSessionAsync();

        response.ShouldBeError("invalid_token",
            "SERVER_SPEC.md § 3: a well-formed but unknown or expired token is 401 invalid_token — a " +
            "different code from 'no credential at all', so a client knows whether to re-pair.");
    }

    /// <summary>
    /// § 3: "A token in a query string MUST be ignored, and the request MUST be treated as
    /// unauthenticated." The reason is in SERVER_PLAN.md § 3.5 — query strings land in logs and
    /// proxy history, and this token grants the filesystem.
    /// </summary>
    [Fact]
    public async Task A_token_in_the_query_string_does_not_authenticate()
    {
        var credentials = await Server.AuthenticateAsync();
        if (credentials.Mode != AuthMode.Bearer)
            throw new Xunit.Sdk.XunitException(
                "This test needs a real token to smuggle into a query string.\n" + credentials.Diagnostic);

        var token = Uri.EscapeDataString(credentials.Token!);

        foreach (var path in new[]
                 {
                     $"/session?token={token}",
                     $"/session?access_token={token}",
                     $"/libraries/roots?token={token}"
                 })
        {
            var response = await Anonymous.GetAsync(path, authenticate: false);
            response.ShouldBeError("unauthenticated",
                $"SERVER_SPEC.md § 3: a token in the query string is ignored and the request is " +
                $"unauthenticated. '{path}' must not work.");
        }
    }

    [Fact]
    public async Task Revoking_a_device_does_not_close_the_session()
    {
        var credentials = await Server.AuthenticateAsync();
        if (credentials.Mode != AuthMode.Bearer)
            throw new Xunit.Sdk.XunitException("This test needs a bearer token.\n" + credentials.Diagnostic);

        using var folder = Fixtures.LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        // A device id that was never issued: § 10.12 makes that a 404 in the standard envelope.
        var response = await client.RevokeAsync("device-that-does-not-exist");
        response.ShouldBeError("not_found",
            "SERVER_SPEC.md § 10.12: DELETE /pair/{deviceId} for an unknown device is 404 not_found.");

        var after = await client.GetSessionAsync();
        var snapshot = after.ShouldBeSnapshot(200,
            "SERVER_SPEC.md § 10.12: revocation does not close a session — only DELETE /session does");

        Assert.Equal(opened.SessionId, snapshot.SessionId);
    }

    [Fact]
    public async Task Pairing_and_ping_are_the_only_endpoints_open_without_a_token()
    {
        var ping = await Anonymous.PingAsync(authenticate: false);
        ping.ShouldHaveStatus(200, "SERVER_SPEC.md § 3: GET /ping without a token returns the public subset");

        // POST /pair must not answer 401: the pairing code is the credential (§ 3). With no window
        // open it is 403 pairing_not_open, and with a wrong code 401 invalid_pairing_code — but
        // never 'unauthenticated', which would mean it wanted a bearer token.
        var pair = await Anonymous.PairAsync("000 000", "harness");
        Assert.True(pair.StatusCode != 401 ||
                    pair.Json?.GetProperty("error").GetProperty("code").GetString() != "unauthenticated",
            "SERVER_SPEC.md § 3: POST /pair takes no bearer token — a valid pairing code is the credential.\n" +
            pair.Describe());
    }
}
