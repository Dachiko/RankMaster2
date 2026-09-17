using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RankMaster2.Server.Security;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>
/// H12, closed by S-SECURITY. The audit's original probes here (2026-09-17) each documented a
/// defect by passing: a stranger's five wrong guesses killed the owner's own pairing window, and a
/// <c>devices.json</c> that would not parse both logged out every device and opened a fresh pairing
/// window by itself. Both are flipped below to assert the fixed behaviour SERVER_SPEC.md §§ 10.11
/// and 2.4 now describe; a regression makes them fail.
/// </summary>
public sealed class PairingWindowSecurityTests(Rm2Server server) : AuditTestBase(server)
{
    /// <summary>
    /// SERVER_SPEC.md § 10.11: the five-guess budget is per source address, per window — never a
    /// total shared across every caller. This is exercised directly against <see cref="PairingService"/>
    /// rather than over HTTP because the test harness's in-process server gives every request the
    /// same connection, so there is no way to present two source addresses through <c>Rm2Client</c>.
    /// </summary>
    [Fact]
    public void Five_wrong_guesses_from_one_address_leave_another_addresses_budget_and_the_window_intact()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-pairing-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = new Rm2SecurityOptions { DataDirectory = dataDir };
            var tokens = TokenStore.Open(dataDir);
            var limiter = new PairingRateLimiter(perMinute: 1000); // the rate limit is not what this proves
            var pairing = new PairingService(options, tokens, limiter, dataDir, "deadbeef", "127.0.0.1", 18611);

            var now = DateTimeOffset.UtcNow;
            var offer = pairing.OpenWindow(now);
            var wrong = offer.Code == "000000" ? "111111" : "000000";

            const string stranger = "203.0.113.9";
            const string owner = "192.168.1.50";

            PairAttemptResult fromStranger = null!;
            for (var i = 0; i < 5; i++)
                fromStranger = pairing.Redeem(wrong, "stranger", stranger, now);

            Assert.Equal(PairOutcome.InvalidCode, fromStranger.Outcome);
            Assert.Equal(0, fromStranger.AttemptsRemaining);

            // The stranger's own address is now locked out even against the real code — but that is
            // the only thing five wrong guesses bought them.
            var strangerWithRealCode = pairing.Redeem(offer.Code, "stranger", stranger, now);
            Assert.Equal(PairOutcome.InvalidCode, strangerWithRealCode.Outcome);

            // The owner's address never guessed here: a first wrong guess from it reports a fresh
            // budget of four remaining, not "window dead" — the stranger's five did not touch it.
            var ownerFirstWrong = pairing.Redeem(wrong, "owner phone", owner, now);
            Assert.Equal(PairOutcome.InvalidCode, ownerFirstWrong.Outcome);
            Assert.Equal(4, ownerFirstWrong.AttemptsRemaining);

            // And the window itself was never destroyed: the owner's real code still redeems.
            var paired = pairing.Redeem(offer.Code, "owner phone", owner, now);
            Assert.Equal(PairOutcome.Paired, paired.Outcome);
            Assert.NotNull(paired.Issued);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A32: if persisting devices.json throws while redeeming a correct code, the window must not
    /// have been spent for a token that was never actually issued — a retry with the same code must
    /// still work. Forces the failure by making the data directory unwritable, a POSIX technique;
    /// every environment this suite runs in is Linux (nothing Windows runs here — § 6).
    /// </summary>
    [Fact]
    public void A_failed_persist_during_redeem_leaves_the_window_valid_for_a_retry()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-pairing-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = new Rm2SecurityOptions { DataDirectory = dataDir };
            var tokens = TokenStore.Open(dataDir);
            var limiter = new PairingRateLimiter(perMinute: 1000);
            var pairing = new PairingService(options, tokens, limiter, dataDir, "deadbeef", "127.0.0.1", 18611);

            var now = DateTimeOffset.UtcNow;
            var offer = pairing.OpenWindow(now);

            // devices.json does not exist yet (nobody has paired), so TokenStore.Issue's persist has
            // to create it — which fails the moment the directory itself is not writable.
            File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.ThrowsAny<Exception>(() => pairing.Redeem(offer.Code, "phone", "192.168.1.1", now));
            }
            finally
            {
                File.SetUnixFileMode(dataDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // The window must not have been marked spent by the failed attempt: the same code pairs
            // once the disk works again, rather than the caller finding "invalid_pairing_code" for a
            // token that was never issued.
            var retry = pairing.Redeem(offer.Code, "phone", "192.168.1.1", now);
            Assert.Equal(PairOutcome.Paired, retry.Outcome);
            Assert.NotNull(retry.Issued);
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(dataDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(dataDir, recursive: true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// SERVER_SPEC.md § 2.4 / TokenStore.Load: a devices.json that will not parse is moved aside
    /// (never silently emptied in place) and does not, by itself, open a pairing window — only a
    /// genuinely absent store does that. Reproduced end to end with a second in-process server over
    /// the same data directory, as the original audit probe did.
    /// </summary>
    [Fact]
    public async Task A_corrupt_device_store_is_moved_aside_and_does_not_open_pairing_by_itself()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-audit-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        string token;
        using (var first = Factory(dataDir))
        {
            var http = first.CreateClient();
            var anon = new Rm2Client(http);
            var code = await WaitForOfferCode(dataDir);
            Assert.NotNull(code);   // fresh install: window auto-opened
            var paired = await anon.PairAsync(code!, "audit phone");
            paired.ShouldHaveStatus(201, "pair against the auto-opened first-run window");
            token = paired.Json!.Value.GetProperty("token").GetString()!;
        }

        var devicesPath = Path.Combine(dataDir, "devices.json");
        Assert.True(File.Exists(devicesPath));
        await File.WriteAllTextAsync(devicesPath, "{ this is not json");

        using (var second = Factory(dataDir))
        {
            var http = second.CreateClient();
            var withToken = new Rm2Client(http, token);

            var ping = await withToken.PingAsync();
            ping.ShouldHaveStatus(401, "the token that was valid before the file was damaged is now unknown");
            Assert.Equal("invalid_token", ping.ErrorCode);

            // Moved aside, not deleted and not left in place: the original bytes are still there,
            // just not at devices.json any more.
            Assert.False(File.Exists(devicesPath), "a devices.json that would not parse must not be left in place");
            var quarantined = Directory.GetFiles(dataDir, "devices.json.corrupt-*");
            Assert.Single(quarantined);
            Assert.Contains("this is not json", await File.ReadAllTextAsync(quarantined[0]));

            // And no window opened by itself: a damaged store is not the same thing as a fresh
            // install, so it must not double as a way to force pairing open.
            var code = await WaitForOfferCode(dataDir, TimeSpan.FromSeconds(3));
            Assert.Null(code);
        }

        try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// AUDIT2.md § 3.2 / § 3.3. One address exhausting its own five-guess budget must lock out only
    /// that address (proven already, above) — but until this fix it also took <c>pairing.json</c>
    /// down with it, which let a single stranger repeat that forever (§ 3.3: "he clicks 'New code';
    /// the stranger spends another five; repeat") and made the tray misreport the one other thing
    /// that removes the file — a successful pair — as an attack every single time (§ 3.2). Proven
    /// directly against <see cref="PairingService"/>, like the budget test above.
    /// </summary>
    [Fact]
    public void An_exhausted_address_does_not_delete_the_offer_file_and_only_a_successful_pair_does()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-pairing-offerfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = new Rm2SecurityOptions { DataDirectory = dataDir };
            var tokens = TokenStore.Open(dataDir);
            var limiter = new PairingRateLimiter(perMinute: 1000);
            var pairing = new PairingService(options, tokens, limiter, dataDir, "deadbeef", "127.0.0.1", 18611);

            var now = DateTimeOffset.UtcNow;
            var offer = pairing.OpenWindow(now);
            Assert.True(File.Exists(pairing.OfferFilePath), "OpenWindow must publish the offer file");

            var wrong = offer.Code == "000000" ? "111111" : "000000";
            const string stranger = "203.0.113.9";
            for (var i = 0; i < 5; i++)
                pairing.Redeem(wrong, "stranger", stranger, now);

            Assert.True(File.Exists(pairing.OfferFilePath),
                "AUDIT2.md § 3.3: a stranger exhausting their own address's budget must not delete " +
                "pairing.json — the owner's own QR/code must still be readable afterwards, and it must " +
                "not be repeatable to deny him a second time with a fresh window");

            // The window itself still redeems for a different address, exactly as the per-address
            // budget always intended.
            var paired = pairing.Redeem(offer.Code, "owner phone", "192.168.1.50", now);
            Assert.Equal(PairOutcome.Paired, paired.Outcome);

            // Only the successful pair takes the file down — the one event the tray should ever
            // read as "the file is gone" (AUDIT2.md § 3.2).
            Assert.False(File.Exists(pairing.OfferFilePath),
                "a successful pairing still removes the offer file — it is now the only thing that does");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// AUDIT2.md § 3.13: <c>pair.request</c> used to be consumed (deleted) the instant it was found,
    /// with no regard for how long it had been sitting there — so a request already answered by an
    /// earlier poll and left behind, or one dropped by a tray that crashed before it could clean up
    /// after itself, could re-open an unattended window at a much later, unrelated server start. A
    /// fresh sentinel is still honoured; a stale one is consumed (never fires twice) but reported
    /// separately so the caller knows not to open anything for it.
    /// </summary>
    [Fact]
    public void A_fresh_pair_request_opens_and_a_stale_one_is_dropped_without_opening()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-pairing-request-age-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = new Rm2SecurityOptions { DataDirectory = dataDir };
            var tokens = TokenStore.Open(dataDir);
            var limiter = new PairingRateLimiter(perMinute: 1000);
            var pairing = new PairingService(options, tokens, limiter, dataDir, "deadbeef", "127.0.0.1", 18611);
            var requestPath = pairing.RequestFilePath;

            Assert.Equal(PairRequestPickup.None, pairing.ConsumePairRequestFile(DateTimeOffset.UtcNow));

            File.WriteAllBytes(requestPath, []);
            Assert.Equal(PairRequestPickup.Fresh, pairing.ConsumePairRequestFile(DateTimeOffset.UtcNow));
            Assert.False(File.Exists(requestPath), "a fresh request is consumed once picked up");

            File.WriteAllBytes(requestPath, []);
            var stampedOld = DateTime.UtcNow - PairingService.MaxPairRequestAge - TimeSpan.FromSeconds(10);
            File.SetLastWriteTimeUtc(requestPath, stampedOld);

            Assert.Equal(PairRequestPickup.Stale, pairing.ConsumePairRequestFile(DateTimeOffset.UtcNow));
            Assert.False(File.Exists(requestPath),
                "a stale request must still be consumed — never left to be picked up again later");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// AUDIT2.md § 3.13, end to end: a <c>pair.request</c> left behind by a process that crashed
    /// before it could clean up after itself must not open a live, unattended pairing window at the
    /// next, unrelated server start. The first start pairs a device (so the second start is not a
    /// fresh install, and would not auto-open a window by itself); a stale sentinel is then dropped
    /// where the crashed process would have left it, and the second start proves no window opens.
    /// </summary>
    [Fact]
    public async Task A_pair_request_left_behind_by_a_crash_does_not_open_a_window_at_the_next_start()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-audit-crash-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            using (var first = Factory(dataDir))
            {
                var anon = new Rm2Client(first.CreateClient());
                var code = await WaitForOfferCode(dataDir);
                Assert.NotNull(code);   // fresh install: window auto-opened
                (await anon.PairAsync(code!, "audit phone")).ShouldHaveStatus(201, "first-run pair");
            }

            var requestPath = Path.Combine(dataDir, "pair.request");
            File.WriteAllBytes(requestPath, []);
            File.SetLastWriteTimeUtc(requestPath,
                DateTime.UtcNow - PairingService.MaxPairRequestAge - TimeSpan.FromSeconds(10));

            using (var second = Factory(dataDir))
            {
                _ = second.CreateClient(); // starts the host

                // Long enough for several 1 s polls; no window must appear.
                var code = await WaitForOfferCode(dataDir, TimeSpan.FromSeconds(4));
                Assert.Null(code);

                // And the stale sentinel was still consumed, not merely ignored in place.
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (File.Exists(requestPath) && DateTime.UtcNow < deadline)
                    await Task.Delay(150);
                Assert.False(File.Exists(requestPath), "the stale sentinel must be consumed even though it is dropped");
            }
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// AUDIT2.md § 3.13: the out-of-band channel for revoking a device other than the caller's own —
    /// the same OS-account proof opening a pairing window needs (§ 10.1.1), since a bearer token
    /// alone is not proof of the owner's say-so over a *different* device
    /// (<c>SecurityEndpoints</c>'s <c>DELETE /pair/{deviceId}</c> now refuses that; see
    /// <c>AuthGateTests</c>/<c>AuthenticationTests</c>). Proven end to end: dropping
    /// <c>revoke.request</c> naming a paired device's id revokes it within the server's own 1 s poll.
    /// </summary>
    [Fact]
    public async Task Writing_revoke_request_revokes_the_named_device()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-audit-revoke-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            using var factory = Factory(dataDir);
            var http = factory.CreateClient();
            var anon = new Rm2Client(http);

            var code = await WaitForOfferCode(dataDir);
            Assert.NotNull(code);
            var paired = await anon.PairAsync(code!, "device to be revoked remotely");
            paired.ShouldHaveStatus(201, "pair against the auto-opened first-run window");
            var deviceId = paired.Json!.Value.GetProperty("deviceId").GetString()!;
            var token = paired.Json!.Value.GetProperty("token").GetString()!;
            var target = anon.WithToken(token);

            (await target.PingAsync()).ShouldHaveStatus(200, "the device is enrolled before the revoke request");

            var revokeRequestPath = Path.Combine(dataDir, "revoke.request");
            await File.WriteAllTextAsync(revokeRequestPath, deviceId);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            Rm2Response? after = null;
            while (DateTime.UtcNow < deadline)
            {
                after = await target.PingAsync();
                if (after.ErrorCode == "token_revoked") break;
                await Task.Delay(150);
            }

            Assert.Equal("token_revoked", after?.ErrorCode);
            Assert.False(File.Exists(revokeRequestPath), "the sentinel is consumed once picked up, like pair.request");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>SERVER_SPEC.md § 4: no snapshot on a 401 even while a session is open.</summary>
    [Fact]
    public async Task A_401_while_a_session_is_open_carries_no_snapshot()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        (await client.OpenSessionAsync(folder.Path)).ShouldHaveStatus(201, "open");

        var bad = client.WithToken("rm2_AAAAAAAAAAAAAAAAAAAAAA.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        var r = await bad.GetSessionAsync();
        r.ShouldHaveStatus(401, "bad token");
        Assert.False(r.Json!.Value.GetProperty("error").TryGetProperty("session", out _));

        var none = await Anonymous.SendAsync(HttpMethod.Post, "/session/vote", new { pairToken = "x", winner = "left" }, authenticate: false);
        none.ShouldHaveStatus(401, "no token");
        Assert.False(none.Json!.Value.GetProperty("error").TryGetProperty("session", out _));
        Assert.DoesNotContain(folder.Path, none.Text);

        await client.CloseSessionAsync();
    }

    private static WebApplicationFactory<Program> Factory(string dataDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RankMaster2:DataDirectory"] = dataDir,
            }));
        });

    private static async Task<string?> WaitForOfferCode(string dataDir, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        var path = Path.Combine(dataDir, "pairing.json");
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (doc.RootElement.TryGetProperty("code", out var c) && c.GetString() is { Length: > 0 } v)
                        return v;
                }
            }
            catch (Exception e) when (e is IOException or JsonException) { }
            await Task.Delay(150);
        }
        return null;
    }
}
