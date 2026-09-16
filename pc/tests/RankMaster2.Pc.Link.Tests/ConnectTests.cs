using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Enrolment;
using RankMaster2.Pc.Link.Tests.Harness;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>§ 6.3 "Connect and enrol" (C1-C8). Each test manufactures its own preconditions (its own
/// credential directory, its own fresh enrolment) rather than depending on another test's leftovers,
/// so these are order-independent even though they share the one <see cref="RealServer"/>. C4, C5 and
/// C7 kill or restart the shared server and always leave it running again before returning.</summary>
[Collection("RealServer")]
public sealed class ConnectTests(RealServer server)
{
    private static string FreshCredentialDir() =>
        Path.Combine(Path.GetTempPath(), "rm2-link-cred", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task C1_ColdConnectEnrolsAndStoresAnOwnerOnlyCredential()
    {
        var credentialDir = FreshCredentialDir();
        var (link, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        var result = await link.ConnectAsync();
        var connected = Assert.IsType<ConnectResult.Connected>(result);
        Assert.True(connected.Enrolled);
        Assert.False(connected.StartedServer);
        Assert.Equal(LinkState.Connected, link.State);

        var credentialPath = Path.Combine(credentialDir, "link.json");
        Assert.True(File.Exists(credentialPath));
        var mode = File.GetUnixFileMode(credentialPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        var credential = Credential.Load(credentialDir)!;
        using var handler = RealServer.PinnedTo(server.Fingerprint);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        var response = await http.GetAsync(server.BaseUrl + "/ping");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"authenticated\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C2_WarmConnectSendsOnlyOnePing()
    {
        var credentialDir = FreshCredentialDir();
        var (firstLink, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using (firstLink) { await firstLink.ConnectAsync(); }

        var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        var result = await link.ConnectAsync();
        var connected = Assert.IsType<ConnectResult.Connected>(result);
        Assert.False(connected.Enrolled);

        var sent = tap.Sent;
        Assert.Single(sent);
        Assert.Equal("GET", sent[0].Method);
        Assert.Equal("/ping", sent[0].Path);
        Assert.True(sent[0].BearerPresent);
    }

    [Fact]
    public async Task C3_AddressChangedTriggersReEnrolmentAndRevokesOldDevice()
    {
        var credentialDir = FreshCredentialDir();
        var (firstLink, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using (firstLink) { await firstLink.ConnectAsync(); }

        var original = Credential.Load(credentialDir)!;
        var wrong = original with { BaseUrl = $"https://127.0.0.1:1/api/v1" };
        wrong.Save(credentialDir);

        var (link, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        var result = await link.ConnectAsync();
        var connected = Assert.IsType<ConnectResult.Connected>(result);
        Assert.True(connected.Enrolled);

        var updated = Credential.Load(credentialDir)!;
        Assert.Equal(server.BaseUrl, updated.BaseUrl);
        Assert.NotEqual(original.DeviceId, updated.DeviceId);

        using var handler = RealServer.PinnedTo(server.Fingerprint);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.Token);
        var response = await http.GetAsync(server.BaseUrl + "/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task C4_ServerNotRunningStartAllowed()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "rm2-pid-" + Guid.NewGuid().ToString("N"));
        var (executable, arguments) = ServerLaunch.For(server.DotnetPath, server.ServerDllPath, server.DataDirectory, server.Port, pidFile);

        var options = LinkFactory.DefaultOptions(server) with
        {
            StartServerIfNotRunning = true,
            ServerExecutable = executable,
            ServerArguments = arguments,
            ServerStartTimeout = TimeSpan.FromSeconds(25),
            OfferTimeout = TimeSpan.FromSeconds(5),
        };
        var (link, _) = LinkFactory.Build(server, options);
        await using var linkScope = link;

        server.Kill();

        var result = await link.ConnectAsync();

        // The started process is adopted first, whatever the outcome, so a failed assertion below
        // still leaves the fixture's server running for later tests.
        if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid))
            server.AdoptPid(pid);

        var connected = Assert.IsType<ConnectResult.Connected>(result);
        Assert.True(connected.StartedServer);
    }

    [Fact]
    public async Task C5_ServerNotRunningStartRefused()
    {
        try
        {
            var options = LinkFactory.DefaultOptions(server) with { StartServerIfNotRunning = false, OfferTimeout = TimeSpan.FromSeconds(3) };
            var (link, tap) = LinkFactory.Build(server, options);
            await using var linkScope = link;

            server.Kill();

            var result = await link.ConnectAsync();
            var failed = Assert.IsType<ConnectResult.Failed>(result);
            Assert.Equal(FailureKind.ServerNotRunning, failed.Failure.Kind);
            Assert.Contains(server.DataDirectory, failed.Failure.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(tap.Sent, s => s.Path.Contains("/pair", StringComparison.Ordinal));
        }
        finally
        {
            await server.RestartAsync();
        }
    }

    [Fact]
    public async Task C6_WrongServerNeverSendsTheBearerToken()
    {
        await using var stub = StubTlsServer.Start();

        var credentialDir = FreshCredentialDir();
        var bogus = new Credential(
            $"https://127.0.0.1:{stub.Port}/api/v1", server.Fingerprint, "some-token", "device-x",
            DateTimeOffset.UtcNow.ToString("o"));
        bogus.Save(credentialDir);

        var (link, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        var result = await link.ConnectAsync();
        var failed = Assert.IsType<ConnectResult.Failed>(result);
        Assert.Equal(FailureKind.NotYourServer, failed.Failure.Kind);
        Assert.True(failed.Failure.Fatal);

        await Task.Delay(300);
        Assert.False(stub.SawApplicationBytes);
    }

    [Fact]
    public async Task C7_ReMintedCertificateIsAdoptedAutomatically()
    {
        var credentialDir = FreshCredentialDir();
        var (firstLink, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using (firstLink) { await firstLink.ConnectAsync(); }

        var certificatePath = Path.Combine(server.DataDirectory, "certificate.pfx");
        File.Delete(certificatePath);
        await server.RestartAsync();

        var (link, _) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        var result = await link.ConnectAsync();
        var connected = Assert.IsType<ConnectResult.Connected>(result);
        Assert.True(connected.Enrolled);
    }

    [Fact]
    public async Task C8_PairingCodeSpentAsksForAFreshWindowOnce()
    {
        {
            var credentialDir = FreshCredentialDir();
            var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
            await using var linkScope = link;

            tap.Script(HttpMethod.Post, "/pair",
                ScriptedFault.Fabricate(401, """{"error":{"code":"invalid_pairing_code","message":"nope","requestId":"r1","details":{"attemptsRemaining":4}}}"""),
                ScriptedFault.Forward);

            var result = await link.ConnectAsync();
            var connected = Assert.IsType<ConnectResult.Connected>(result);
            Assert.True(connected.Enrolled);
            Assert.Equal(2, tap.Sent.Count(s => s.Path == "/pair"));
        }
        {
            var credentialDir = FreshCredentialDir();
            var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
            await using var linkScope = link;

            tap.Script(HttpMethod.Post, "/pair",
                ScriptedFault.Fabricate(401, """{"error":{"code":"invalid_pairing_code","message":"nope","requestId":"r1","details":{"attemptsRemaining":4}}}"""),
                ScriptedFault.Fabricate(401, """{"error":{"code":"invalid_pairing_code","message":"nope","requestId":"r2","details":{"attemptsRemaining":0}}}"""));

            var result = await link.ConnectAsync();
            var failed = Assert.IsType<ConnectResult.Failed>(result);
            Assert.Equal(FailureKind.PairingFailed, failed.Failure.Kind);
            Assert.Equal(2, tap.Sent.Count(s => s.Path == "/pair"));
        }
    }
}
