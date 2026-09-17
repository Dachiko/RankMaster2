using System.Text.Json;
using System.Text.RegularExpressions;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// `GET /ping` (SERVER_SPEC.md § 14). It is the only endpoint a client can reach before it has a
/// token, which makes it the one place the certificate fingerprint can come from — so its public
/// subset is load-bearing, not a convenience.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class PingContractTests(Rm2Server server) : SessionTestBase(server)
{
    [Fact]
    public async Task Public_subset_needs_no_token_and_hides_the_session()
    {
        var response = await server.Anonymous.PingAsync(authenticate: false);
        response.ShouldHaveStatus(200, "GET /ping without a token returns the public subset (SERVER_SPEC.md § 3)");

        var body = response.JsonBody;
        ContractShape.RequireExactKeys(body, "Ping", ContractShape.PingKeys, "GET /ping (SERVER_SPEC.md § 14)", response);

        Assert.False(body.GetProperty("authenticated").GetBoolean());

        if (body.GetProperty("session").ValueKind != JsonValueKind.Null)
            throw response.Failure(
                "SERVER_SPEC.md § 14: `session` is null in the public subset. An unauthenticated caller " +
                "must not learn which folder is open.");
    }

    [Fact]
    public async Task An_invalid_token_is_rejected_rather_than_downgraded()
    {
        var response = await server.Anonymous.WithToken("not-a-real-token").PingAsync();

        // § 3: "With an *invalid* token it MUST return 401, not the public subset — a client with a
        // bad token must learn that it is bad."
        response.ShouldBeErrorOneOf(
            "GET /ping with a bad token must be 401, not a silent downgrade to the public subset (SERVER_SPEC.md § 3)",
            "invalid_token", "unauthenticated");
    }

    [Fact]
    public async Task The_certificate_fingerprint_is_pinnable()
    {
        var response = await server.Anonymous.PingAsync(authenticate: false);
        response.ShouldHaveStatus(200, "GET /ping");

        var fingerprint = response.JsonBody.GetProperty("certificateFingerprint").GetString();
        Assert.True(fingerprint is not null && Regex.IsMatch(fingerprint, "^sha256:[0-9a-f]{64}$"),
            $"certificateFingerprint must be 'sha256:' plus 64 lowercase hex characters (openapi.yaml, " +
            $"SERVER_SPEC.md § 14). Got '{fingerprint}'. This is what the client pins; a self-signed " +
            "certificate with nothing to pin is theatre.");
    }

    [Fact]
    public async Task The_absent_features_are_absent_and_not_configurable()
    {
        var response = await server.Anonymous.PingAsync(authenticate: false);
        response.ShouldHaveStatus(200, "GET /ping");

        var features = response.JsonBody.GetProperty("features");
        ContractShape.RequireExactKeys(features, "PingFeatures", ContractShape.PingFeatureKeys,
                                       "GET /ping (SERVER_SPEC.md § 14)", response);

        // SERVER_PLAN.md § 7 fixes all of these. Rename is reversed (2026-09-16): it now lives on
        // the server as a journalled, long-running operation (SERVER_SPEC.md § 10.16); every other
        // absent feature stands.
        Assert.True(features.GetProperty("rename").GetBoolean(),
            "features.rename is true — rename-by-rank is a journalled server operation (SERVER_SPEC.md § 10.16).");
        Assert.False(features.GetProperty("videoTranscoding").GetBoolean());
        Assert.False(features.GetProperty("posterFrames").GetBoolean());
        Assert.False(features.GetProperty("videoProbe").GetBoolean(),
            "The server never decodes a video frame (SERVER_SPEC.md § 1.1).");
        Assert.Equal("full-filesystem", features.GetProperty("browse").GetString());
        Assert.Equal(1, features.GetProperty("maxConcurrentSessions").GetInt32());
    }

    [Fact]
    public async Task The_limits_it_advertises_are_the_limits_the_spec_sets()
    {
        var response = await server.Anonymous.PingAsync(authenticate: false);
        response.ShouldHaveStatus(200, "GET /ping");

        var limits = response.JsonBody.GetProperty("limits");
        ContractShape.RequireExactKeys(limits, "PingLimits", ContractShape.PingLimitKeys,
                                       "GET /ping (SERVER_SPEC.md § 14)", response);

        var widths = limits.GetProperty("stillWidths").EnumerateArray().Select(w => w.GetInt32()).ToArray();
        Assert.Equal(new[] { 360, 540, 720, 1080, 1440, 2160 }, widths);
        Assert.Equal(320, limits.GetProperty("thumbWidth").GetInt32());
        Assert.Equal(65536, limits.GetProperty("maxJsonBodyBytes").GetInt32());
        Assert.Equal(5, limits.GetProperty("sessionLockTimeoutSeconds").GetInt32());
    }

    /// <summary>
    /// SERVER_SPEC.md § 14 and <c>openapi.yaml</c>: with a session open, the authenticated ping's
    /// <c>session</c> block is <c>{ open, sessionId, folder, state }</c> — every member required,
    /// nulls written as null and never omitted. With none open it is
    /// <c>{ open: false, null, null, null }</c>.
    ///
    /// <para>This test used to guard its one structural assertion with
    /// <c>if (session.ValueKind == JsonValueKind.Object)</c> and assert no field at all, so it passed
    /// green for as long as the server answered <c>{'open': true, 'sessionId': null, 'folder': …,
    /// 'state': null}</c> — which is what it had been answering, because <c>/ping</c> was fed by a
    /// reflection bridge that could read only the folder (AUDIT.md A4, C15, T1a). The <c>if</c> is
    /// gone and the fields are named.</para>
    /// </summary>
    [Fact]
    public async Task Authenticated_ping_summarises_the_open_session()
    {
        var credentials = await server.AuthenticateAsync();
        if (credentials.Mode != AuthMode.Bearer)
            throw new Xunit.Sdk.XunitException(
                "This test needs a bearer token.\n" + credentials.Diagnostic);

        using var folder = Fixtures.LibraryFolder.SixStills();
        var client = await server.AuthenticatedAsync();

        var snapshot = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
        try
        {
            var response = await client.PingAsync();
            response.ShouldHaveStatus(200, "GET /ping with a valid token");

            var body = response.JsonBody;
            Assert.True(body.GetProperty("authenticated").GetBoolean(),
                "An authenticated ping reports authenticated: true (SERVER_SPEC.md § 14).");

            var session = body.GetProperty("session");
            Assert.True(session.ValueKind == JsonValueKind.Object,
                "SERVER_SPEC.md § 14: an authenticated ping while a session is open carries the session " +
                $"block. Got {session.ValueKind}: {response.Text}");

            ContractShape.RequireExactKeys(session, "PingSession", ContractShape.PingSessionKeys,
                                           "GET /ping (SERVER_SPEC.md § 14)", response);

            Assert.True(session.GetProperty("open").GetBoolean(), "a session is open, so open is true");
            Assert.Equal(snapshot.SessionId, session.GetProperty("sessionId").GetString());
            Assert.Equal(snapshot.Folder, session.GetProperty("folder").GetString());
            Assert.Equal("ranking", session.GetProperty("state").GetString());
        }
        finally
        {
            (await client.CloseSessionAsync()).ShouldHaveStatus(204, "close");
        }

        // And with nothing open: open false, the other three null — written, not omitted.
        var closed = await client.PingAsync();
        closed.ShouldHaveStatus(200, "GET /ping after the session closed");

        var closedSession = closed.JsonBody.GetProperty("session");
        Assert.True(closedSession.ValueKind == JsonValueKind.Object,
            $"SERVER_SPEC.md § 14: the session block is an object even with nothing open. Got {closed.Text}");
        ContractShape.RequireExactKeys(closedSession, "PingSession", ContractShape.PingSessionKeys,
                                       "GET /ping (SERVER_SPEC.md § 14)", closed);

        Assert.False(closedSession.GetProperty("open").GetBoolean());
        foreach (var field in new[] { "sessionId", "folder", "state" })
        {
            Assert.True(closedSession.GetProperty(field).ValueKind == JsonValueKind.Null,
                $"SERVER_SPEC.md § 14: with no session open, session.{field} is null. Got {closed.Text}");
        }

        // The exhausted state reaches the ping too — it is `state`, not a second "is it open" flag.
        using var two = Fixtures.LibraryFolder.TwoStills();
        var opened = (await client.OpenSessionAsync(two.Path)).ShouldBeSnapshot(201, "open a two-file folder");
        try
        {
            (await client.DiscardAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "discard");
            var exhausted = await client.PingAsync();
            Assert.Equal("exhausted", exhausted.JsonBody.GetProperty("session").GetProperty("state").GetString());
        }
        finally
        {
            await client.CloseSessionAsync();
        }
    }

    [Fact]
    public async Task Json_responses_are_never_stored()
    {
        var response = await server.Anonymous.PingAsync(authenticate: false);
        var cacheControl = response.HeaderOrNull("Cache-Control");

        Assert.True(cacheControl is not null && cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase),
            "SERVER_SPEC.md § 2: every JSON response carries Cache-Control: no-store. Media responses are " +
            $"the only cacheable ones. Got '{cacheControl ?? "(absent)"}'.");
    }
}
