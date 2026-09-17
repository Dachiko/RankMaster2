using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>
/// Throwaway audit probes (2026-09-17). Each test documents an observed behaviour; the assertions
/// pin down what the server does today so the audit can cite a green test as evidence.
/// </summary>
public sealed class Audit_PairingAndGate(Rm2Server server) : AuditTestBase(server)
{
    /// <summary>
    /// Anyone on the LAN can spend the per-window budget of 5 attempts. Once it is gone the owner's
    /// correct code is refused too. Observed: pairing can be denied by a stranger who merely polls.
    /// </summary>
    [Fact]
    public async Task Five_wrong_guesses_from_a_stranger_destroy_the_owners_window()
    {
        var code = await TestAuth.RequestFreshWindowAsync(Server.DataDirectory, TimeSpan.FromSeconds(15));
        Assert.NotNull(code);

        int? remaining = null;
        var wrong = 0;
        while (wrong < 5)
        {
            var r = await Anonymous.PairAsync("000000", "stranger");
            if (r.StatusCode == 429)
            {
                var wait = int.TryParse(r.HeaderOrNull("Retry-After"), out var s) ? s : 60;
                await Task.Delay(TimeSpan.FromSeconds(wait + 1));
                continue;
            }

            r.ShouldHaveStatus(401, "a wrong guess while a window is open is 401 invalid_pairing_code");
            Assert.Equal("invalid_pairing_code", r.ErrorCode);
            remaining = r.Json!.Value.GetProperty("error").GetProperty("details").GetProperty("attemptsRemaining").GetInt32();
            wrong++;
        }

        Assert.Equal(0, remaining);

        // The owner now tries the real code. Wait out the per-address limit first.
        Rm2Response owner;
        while (true)
        {
            owner = await Anonymous.PairAsync(code!, "owner phone");
            if (owner.StatusCode != 429) break;
            var wait = int.TryParse(owner.HeaderOrNull("Retry-After"), out var s) ? s : 60;
            await Task.Delay(TimeSpan.FromSeconds(wait + 1));
        }

        owner.ShouldHaveStatus(401, "the window was destroyed by the stranger's five guesses; the correct code is refused");
        Assert.Equal("invalid_pairing_code", owner.ErrorCode);

        // And the offer file the tray/QR would show is already gone.
        Assert.False(File.Exists(Path.Combine(Server.DataDirectory, "pairing.json")),
            "pairing.json is deleted when the budget hits zero, so the QR on screen is dead without saying so");
    }

    /// <summary>
    /// A devices.json that will not parse is treated as "no device enrolled": every token dies AND a
    /// pairing window opens by itself at the next start. Reproduced end to end with a second in-process
    /// server over the same data directory.
    /// </summary>
    [Fact]
    public async Task A_corrupt_device_store_kills_every_token_and_opens_pairing_by_itself()
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

        var devices = Path.Combine(dataDir, "devices.json");
        Assert.True(File.Exists(devices));
        await File.WriteAllTextAsync(devices, "{ this is not json");

        using (var second = Factory(dataDir))
        {
            var http = second.CreateClient();
            var withToken = new Rm2Client(http, token);

            var ping = await withToken.PingAsync();
            ping.ShouldHaveStatus(401, "the token that was valid before the file was damaged is now unknown");
            Assert.Equal("invalid_token", ping.ErrorCode);

            var code = await WaitForOfferCode(dataDir);
            Assert.NotNull(code);   // a window opened with nobody asking, because ActiveDeviceCount == 0
        }

        try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// POST /session validates the folder with Path.IsPathRooted + GetFullPath only; /libraries/browse
    /// runs PathGuard (no "..", no UNC, no device names). The same string is refused by one and opened
    /// by the other. On Windows this is what would let a token holder name \\host\share.
    /// </summary>
    [Fact]
    public async Task Session_open_accepts_a_dot_dot_path_that_browse_refuses()
    {
        using var folder = LibraryFolder.SixStills();
        var name = Path.GetFileName(folder.Path);
        var dotted = Path.Combine(folder.Path, "..", name);

        var client = await ClientAsync();
        var browse = await client.BrowseAsync(dotted, counts: false);
        browse.ShouldHaveStatus(400, "PathGuard refuses a relative segment");
        Assert.Equal("invalid_path", browse.ErrorCode);

        var open = await client.OpenSessionAsync(dotted);
        open.ShouldHaveStatus(201, "SessionRegistry.ResolveFolder does not run PathGuard and simply resolves it");
        await client.CloseSessionAsync();
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

    /// <summary>openapi.yaml's /ping example promises sessionId and state in `session`; the reflective
    /// bridge only reads OpenFolder, so both are null even when authenticated with a session open.</summary>
    [Fact]
    public async Task Authenticated_ping_session_block_has_null_sessionId_and_state()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        var ping = await client.PingAsync();
        ping.ShouldHaveStatus(200, "authenticated ping");
        var session = ping.Json!.Value.GetProperty("session");
        Assert.True(session.GetProperty("open").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("sessionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("state").ValueKind);
        Assert.False(string.IsNullOrEmpty(opened.SessionId));

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

    private static async Task<string?> WaitForOfferCode(string dataDir)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
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
