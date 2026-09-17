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

    /// <summary>
    /// SERVER_SPEC.md § 10.12 and § 3: revoking a device makes its next call `401 token_revoked` —
    /// a different code from `invalid_token`, so a client can tell "you were thrown out" from "that
    /// token means nothing here" and stop retrying.
    ///
    /// The device revoked here is a spare, paired at startup for exactly this: revoking it — and it
    /// is revoked only once, by the end of this test — would end the run for any other test that
    /// still expected it enrolled, so both halves of AUDIT2.md § 3.13's fix live in this one test
    /// rather than being split across two that could run in either order: first that a *different*
    /// device cannot revoke it (the vulnerability), then that it can still revoke *itself* (the
    /// feature that must keep working — "Forget this PC").
    /// </summary>
    [Fact]
    public async Task A_revoked_device_is_told_it_was_revoked()
    {
        var spare = Server.SpareDevice;
        if (spare is null)
            throw new Xunit.Sdk.XunitException(
                "This test needs a second paired device and the harness could not pair one. See " +
                $"{nameof(TestAuth)}.{nameof(TestAuth.PairSpareDeviceAsync)}.");

        var spareClient = Anonymous.WithToken(spare.Token);
        var client = await ClientAsync();

        // It works before anything below, or the test proves nothing afterwards.
        var before = await spareClient.PingAsync();
        before.ShouldHaveStatus(200, "the spare device's token works before it is revoked");
        Assert.True(before.JsonBody.GetProperty("authenticated").GetBoolean(),
            "SERVER_SPEC.md § 14: a valid token makes the ping authenticated.");

        // AUDIT2.md § 3.13: the suite's own device — a valid token, just not the spare's own — tries
        // to revoke the spare. That must not be enough: any paired device revoking any other was
        // exactly the hole (a lent phone, one paired once and forgotten, a stolen one, could all log
        // out someone else's device with nothing more than their own still-valid token).
        var crossDevice = await client.RevokeAsync(spare.DeviceId);
        crossDevice.ShouldBeError("not_found",
            "AUDIT2.md § 3.13: a device may revoke itself, never another — naming a different, " +
            "genuinely enrolled device answers the same 404 an unknown one would, not 204.");

        var stillGood = await spareClient.PingAsync();
        stillGood.ShouldHaveStatus(200, "the attempted cross-device revoke must not have taken effect");
        Assert.True(stillGood.JsonBody.GetProperty("authenticated").GetBoolean());

        // Now the spare revokes itself — "Forget this PC" — which must keep working.
        var revoked = await spareClient.RevokeAsync(spare.DeviceId);
        revoked.ShouldHaveStatus(204, "SERVER_SPEC.md § 10.12: a device revoking itself answers 204");

        var after = await spareClient.GetSessionAsync();
        after.ShouldBeError("token_revoked",
            "SERVER_SPEC.md § 3 and § 10.12: the revoked device's next call gets 401 token_revoked. It is a " +
            "distinct code from invalid_token so the client knows it was thrown out rather than mis-configured.");

        // § 3: an invalid token on /ping is a 401, not a quiet downgrade to the public subset.
        var ping = await spareClient.PingAsync();
        ping.ShouldHaveStatus(401,
            "SERVER_SPEC.md § 3: GET /ping with a revoked token is 401, not the public subset — a client with " +
            "a bad token must learn that it is bad");

        // And the suite's own token is untouched, whether by the refused cross-device attempt or by
        // the spare's own successful self-revoke.
        var ours = await client.PingAsync();
        ours.ShouldHaveStatus(200, "revoking one device does not affect another");
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

    /// <summary>
    /// SERVER_SPEC.md § 4: `error.session` MUST be omitted on 401 and 403, and that rule outranks
    /// the "present iff a session is open" one.
    ///
    /// This is the sharpest clause in the document. The snapshot carries the open folder's absolute
    /// path and the filename of everything in it, so attaching it to an authentication failure would
    /// hand a caller who just failed to present a token exactly what the token exists to protect.
    /// A session is deliberately open while this runs, so the test would pass vacuously otherwise.
    /// </summary>
    [Fact]
    public async Task An_authentication_failure_never_leaks_the_open_session()
    {
        using var folder = Fixtures.LibraryFolder.SixStills();
        await OpenAsync(folder);

        var probes = new (string Method, string Path, object? Body, string Description)[]
        {
            ("GET", "/session", null, "no credential"),
            ("POST", "/session/vote", new { pairToken = "x", winner = "left" }, "no credential on an action"),
            ("GET", "/session/pair", null, "no credential on the hot path"),
        };

        foreach (var (method, path, body, description) in probes)
        {
            var response = await Anonymous.SendAsync(new HttpMethod(method), path, body, authenticate: false);
            response.ShouldHaveStatus(401, $"{method} {path} with {description}");

            var error = response.JsonBody.GetProperty("error");
            var leaked = error.TryGetProperty("session", out var session) &&
                         session.ValueKind != System.Text.Json.JsonValueKind.Null;

            Assert.False(leaked,
                $"SERVER_SPEC.md § 4: error.session MUST be omitted on 401. {method} {path} returned a " +
                "snapshot to an unauthenticated caller, which hands out the open folder's absolute path and " +
                "its entire contents — precisely what the bearer token exists to protect.\n" + response.Describe());
        }

        // And the same for a token that is well-formed but not ours.
        var badToken = await Anonymous.WithToken("rm2_not_a_real_token").GetSessionAsync();
        badToken.ShouldHaveStatus(401, "GET /session with an unknown token");

        var bad = badToken.JsonBody.GetProperty("error");
        Assert.False(bad.TryGetProperty("session", out var badSession) &&
                     badSession.ValueKind != System.Text.Json.JsonValueKind.Null,
            "SERVER_SPEC.md § 4: a 401 from a bad token must not carry error.session either.");
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
