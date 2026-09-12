using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// `POST /pair` and `DELETE /pair/{deviceId}` (SERVER_SPEC.md § 10.11, § 10.12).
///
/// The happy path is exercised once for the whole suite by <see cref="TestAuth"/> — a pairing code
/// is single-use, so it cannot be tested repeatedly. What is left, and what these cover, is the
/// refusals: a six-digit code is only safe because a wrong guess costs an attempt and a burst of
/// guesses costs the whole window.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class PairingTests(Rm2Server server)
{
    [Fact]
    public async Task Pairing_succeeded_once_for_this_run()
    {
        var credentials = await server.AuthenticateAsync();

        Assert.True(credentials.Mode == AuthMode.Bearer,
            "SERVER_SPEC.md § 10.11: exchanging a one-time pairing code for a device token is the only way " +
            "in. The harness could not complete it.\n" + credentials.Diagnostic);

        Assert.False(string.IsNullOrWhiteSpace(credentials.Token));
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_says_how_many_attempts_remain()
    {
        var response = await server.Anonymous.PairAsync("000 001", "wrong code");

        var failure = response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 10.11: a wrong, used or expired code is 401 invalid_pairing_code; with no " +
            "window open at all it is 403 pairing_not_open",
            "invalid_pairing_code", "pairing_not_open", "too_many_requests");

        if (failure.Code == "invalid_pairing_code")
        {
            var remaining = failure.Detail("attemptsRemaining", "invalid_pairing_code details").GetInt32();
            Assert.True(remaining >= 0,
                $"SERVER_SPEC.md § 5.1: attemptsRemaining is a count, got {remaining}.");
        }

        if (failure.Code == "too_many_requests")
            Assert.NotNull(response.HeaderOrNull("Retry-After"));
    }

    [Fact]
    public async Task Pairing_needs_a_code()
    {
        var response = await server.Anonymous.SendAsync(HttpMethod.Post, "/pair", new { deviceName = "no code" },
                                                        authenticate: false);

        var failure = response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 10.11 and openapi.yaml: `code` is required on PairRequest",
            "missing_field", "invalid_request", "too_many_requests");

        if (failure.Code == "missing_field")
            Assert.Equal("code", failure.Detail("field", "missing_field details").GetString());
    }

    [Fact]
    public async Task Pairing_refuses_a_body_that_is_not_json()
    {
        var response = await server.Anonymous.SendAsync(HttpMethod.Post, "/pair", null, authenticate: false,
            customise: request => request.Content = new StringContent("code=123456", System.Text.Encoding.UTF8, "text/plain"));

        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 2: a request body that is not application/json is 415",
            "unsupported_content_type", "too_many_requests");
    }

    /// <summary>
    /// § 15: five pairing attempts per minute per source address, then 429 with `Retry-After`.
    /// This is the limit that turns a six-digit code from a one-afternoon brute force into an
    /// impractical one.
    /// </summary>
    [Fact]
    public async Task A_burst_of_wrong_codes_is_rate_limited()
    {
        var sawRateLimit = false;

        for (var attempt = 0; attempt < 12 && !sawRateLimit; attempt++)
        {
            var response = await server.Anonymous.PairAsync($"9999{attempt:D2}", "burst");
            if (response.StatusCode != 429) continue;

            sawRateLimit = true;
            var failure = response.ShouldBeError("too_many_requests",
                "SERVER_SPEC.md § 15: more than 5 pairing attempts per minute per source address is 429");

            Assert.True(failure.Detail("retryAfterSeconds", "too_many_requests details").GetInt32() >= 0);

            var retryAfter = response.HeaderOrNull("Retry-After");
            Assert.True(retryAfter is not null,
                "SERVER_SPEC.md § 6: 503 and 429 MUST carry a Retry-After header in delta-seconds.");
            Assert.True(int.TryParse(retryAfter, out _),
                $"SERVER_SPEC.md § 6: Retry-After is delta-seconds, got '{retryAfter}'.");
        }

        Assert.True(sawRateLimit,
            "SERVER_SPEC.md § 15: rate limiting is REQUIRED on POST /pair — 5 attempts per minute per source " +
            "address. Twelve wrong codes in a row were all answered without a 429. The per-window attempt cap " +
            "may have destroyed the window first, which is also a defence, but the rate limit is the one the " +
            "spec makes mandatory.");
    }
}
