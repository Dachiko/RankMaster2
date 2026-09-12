using System.Text.Json;
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
    /// § 10.11: each pairing window carries a budget of five attempts in total, counted across all
    /// source addresses; on reaching zero the window is destroyed.
    ///
    /// The per-address rate limit alone is walked straight through by an attacker holding several
    /// LAN addresses, which is why the spec makes the per-window budget normative and separate. The
    /// suite has already paired by the time this runs (see <see cref="Harness.Rm2Server"/>), so
    /// spending the window here costs nothing.
    /// </summary>
    [Fact]
    public async Task A_window_has_a_total_attempt_budget_not_just_a_rate_limit()
    {
        // A window with its budget intact, so this does not depend on which pairing test ran first.
        var fresh = await TestAuth.RequestFreshWindowAsync(server.DataDirectory, TimeSpan.FromSeconds(15));
        if (fresh is null)
        {
            throw new Xunit.Sdk.XunitException(
                "Could not get a fresh pairing window through the channel SERVER_SPEC.md § 10.1.1 describes " +
                $"(a sentinel file in '{server.DataDirectory}'), so the per-window budget cannot be observed.");
        }

        var budgets = new List<int>();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            // Wrong by construction: a code that is not the one just issued.
            var wrong = fresh == "111111" ? "222222" : "111111";
            var response = await server.Anonymous.PairAsync(wrong, "budget probe");

            if (response.ErrorCode == "invalid_pairing_code" &&
                response.Json?.GetProperty("error").TryGetProperty("details", out var details) == true &&
                details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("attemptsRemaining", out var remaining))
            {
                budgets.Add(remaining.GetInt32());
                continue;
            }

            // The window died, or the per-address rate limit tripped first. Both are defences, and
            // neither is a reason to keep guessing.
            if (response.ErrorCode is "pairing_not_open" or "too_many_requests") break;
        }

        Assert.True(budgets.Count > 0,
            "SERVER_SPEC.md § 10.11: a wrong code against an open window is 401 invalid_pairing_code with " +
            "details.attemptsRemaining. Not one attempt reported a budget.");

        Assert.True(budgets[0] <= 5,
            "SERVER_SPEC.md § 10.11: a window's total budget is five attempts, counted across all source " +
            $"addresses. The first wrong guess against a fresh window reported {budgets[0]} remaining.");

        Assert.True(budgets.Zip(budgets.Skip(1)).All(pair => pair.Second <= pair.First),
            "SERVER_SPEC.md § 10.11: the per-window budget never rises. It went " +
            $"[{string.Join(", ", budgets)}].");

        Assert.True(budgets.Count < 2 || budgets[^1] < budgets[0],
            "SERVER_SPEC.md § 10.11: each wrong guess costs the window an attempt — a per-address rate limit " +
            "alone is walked straight through by an attacker holding several LAN addresses. The budget did " +
            $"not move: [{string.Join(", ", budgets)}].");

        // Once the budget is gone the window is destroyed, and the correct code is refused thereafter.
        if (budgets[^1] == 0)
        {
            var spent = await server.Anonymous.PairAsync(fresh, "the correct code, after the budget is gone");
            Assert.True(spent.StatusCode != 201,
                "SERVER_SPEC.md § 10.11: on reaching zero the window is destroyed and the correct code is " +
                "refused thereafter. It was accepted.\n" + spent.Describe());
        }
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
