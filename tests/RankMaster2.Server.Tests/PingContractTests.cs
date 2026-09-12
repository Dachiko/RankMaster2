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
public class PingContractTests(Rm2Server server)
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

        // SERVER_PLAN.md § 7 fixes all of these; rename in particular is a product decision.
        Assert.False(features.GetProperty("rename").GetBoolean(),
            "features.rename is always false. There is no build in which rename-by-rank exists (SERVER_SPEC.md § 1.1).");
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

    [Fact]
    public async Task Authenticated_ping_summarises_the_session_without_a_404()
    {
        var credentials = await server.AuthenticateAsync();
        if (credentials.Mode != AuthMode.Bearer)
            throw new Xunit.Sdk.XunitException(
                "This test needs a bearer token.\n" + credentials.Diagnostic);

        var response = await server.Client.PingAsync();
        response.ShouldHaveStatus(200, "GET /ping with a valid token");

        var body = response.JsonBody;
        Assert.True(body.GetProperty("authenticated").GetBoolean(),
            "An authenticated ping reports authenticated: true (SERVER_SPEC.md § 14).");

        var session = body.GetProperty("session");
        if (session.ValueKind == JsonValueKind.Object)
            ContractShape.RequireExactKeys(session, "PingSession", ContractShape.PingSessionKeys,
                                           "GET /ping (SERVER_SPEC.md § 14)", response);
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
