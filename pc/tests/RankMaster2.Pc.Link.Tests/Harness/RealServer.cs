using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>
/// The real Kestrel server, run as a child process, for every test in the suite (§ 6.2). Every fault
/// the negative suite injects sits at the <c>HttpMessageHandler</c> the link owns — the server on the
/// other side is always this real one, so "what the server did" is always checked against its own
/// state (<c>GET /session</c> with a second device's token, and <c>rankmaster_db.json</c> on disk).
/// </summary>
public sealed class RealServer : IAsyncLifetime
{
    private Process? _process;
    private int? _adoptedPid;
    private string _dllPath = "";
    private string _dotnet = "";

    public string DataDirectory { get; private set; } = "";
    public int Port { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";

    /// <summary>Path to the built server DLL. For a test that needs the link to start the server
    /// itself (C4/C5), point <c>LinkOptions.ServerExecutable</c> at <see cref="Repo.FindDotnet"/> and
    /// <c>ServerArguments</c> at <c>[this]</c> — the same code path a published Windows exe takes,
    /// with a different command line (§ 5.2.4).</summary>
    public string ServerDllPath => _dllPath;
    public string DotnetPath => _dotnet;

    public async Task InitializeAsync()
    {
        _dotnet = Repo.FindDotnet();
        _dllPath = LocateOrBuildServerDll();

        DataDirectory = Path.Combine(Path.GetTempPath(), "rm2-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        await StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        Kill();
        try { if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true); }
        catch (IOException) { }
        await Task.CompletedTask;
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    private async Task StartAsync()
    {
        Port = PickFreePort();

        var start = new ProcessStartInfo(_dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(_dllPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(_dllPath);
        start.Environment["RankMaster2__ListenAddress"] = "127.0.0.1";
        start.Environment["RankMaster2__Port"] = Port.ToString();
        start.Environment["RM2_DATA_DIR"] = DataDirectory;
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        // SERVER_SPEC.md § 2.4 / § 13.1: pinned to the shipped defaults rather than inherited, so
        // this suite always exercises the write-behind the owner will actually run — and so a run
        // with RankMaster2__SaveDelaySeconds set in the environment (the server suite's second pass)
        // cannot quietly change what these tests mean.
        start.Environment["RankMaster2__SaveDelaySeconds"] = "2";
        start.Environment["RankMaster2__MaxUnsavedChoices"] = "5";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // The suite pairs and re-pairs many times from the same loopback address; the real 5/minute
        // limit (SERVER_SPEC.md § 15) would starve the run, not the code under test.
        start.Environment["RankMaster2__PairingAttemptsPerMinute"] = "100000";
        start.Environment["RankMaster2__PairingWindowAttempts"] = "1000";

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start RankMaster2.Server.");
        // Drain the redirected streams so the child's pipes never fill up and block it.
        _ = DrainAsync(_process.StandardOutput);
        _ = DrainAsync(_process.StandardError);

        BaseUrl = $"https://127.0.0.1:{Port}/api/v1";

        await WaitForOfferAsync().ConfigureAwait(false);
    }

    /// <summary>SIGKILL, whole tree — no unwinding, no cleanup, exactly what § 5.1.1's N6 needs.</summary>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.WaitForExit(15_000);
        }
        catch (InvalidOperationException) { }
        finally { _process = null; }

        if (_adoptedPid is { } pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
            finally { _adoptedPid = null; }
        }
    }

    /// <summary>Same port, same data directory, so the certificate and tokens survive — as they
    /// would on the owner's PC (§ 6.2).</summary>
    public async Task RestartAsync()
    {
        Kill();
        var start = new ProcessStartInfo(_dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(_dllPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(_dllPath);
        start.Environment["RankMaster2__ListenAddress"] = "127.0.0.1";
        start.Environment["RankMaster2__Port"] = Port.ToString();
        start.Environment["RM2_DATA_DIR"] = DataDirectory;
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        // SERVER_SPEC.md § 2.4 / § 13.1: pinned to the shipped defaults rather than inherited, so
        // this suite always exercises the write-behind the owner will actually run — and so a run
        // with RankMaster2__SaveDelaySeconds set in the environment (the server suite's second pass)
        // cannot quietly change what these tests mean.
        start.Environment["RankMaster2__SaveDelaySeconds"] = "2";
        start.Environment["RankMaster2__MaxUnsavedChoices"] = "5";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // The suite pairs and re-pairs many times from the same loopback address; the real 5/minute
        // limit (SERVER_SPEC.md § 15) would starve the run, not the code under test.
        start.Environment["RankMaster2__PairingAttemptsPerMinute"] = "100000";
        start.Environment["RankMaster2__PairingWindowAttempts"] = "1000";

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not restart RankMaster2.Server.");
        _ = DrainAsync(_process.StandardOutput);
        _ = DrainAsync(_process.StandardError);

        await WaitForReadyAsync().ConfigureAwait(false);
    }

    /// <summary>Adopts an already-started process (e.g. one the link itself started via
    /// <c>ServerProcess.Start</c> in a C4-style test) so <see cref="DisposeAsync"/> still cleans it up.</summary>
    public void Adopt(Process process) => _process = process;

    /// <summary>Adopts a process this fixture never held a <see cref="Process"/> handle for — the
    /// link started it itself (§ 5.2.4), and <c>ServerProcess.Start</c>'s return value is not part of
    /// the public seam. <see cref="ServerLaunch.For"/> writes the PID for exactly this.</summary>
    public void AdoptPid(int pid) => _adoptedPid = pid;

    private static async Task DrainAsync(StreamReader reader)
    {
        try { await reader.ReadToEndAsync().ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- waiting for the server to be reachable -------------------------------------------------

    private async Task WaitForOfferAsync()
    {
        var offerPath = Path.Combine(DataDirectory, "pairing.json");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"RankMaster2.Server exited early with code {_process.ExitCode}.");

            if (File.Exists(offerPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(offerPath));
                    Fingerprint = document.RootElement.GetProperty("certificateFingerprint").GetString()!;
                    return;
                }
                catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException)
                {
                    // half-written; keep polling
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("The server did not publish pairing.json within 20 s (it auto-opens a window when no device is enrolled).");
    }

    private async Task WaitForReadyAsync()
    {
        // After a restart the certificate is already on disk; pairing.json is only rewritten if the
        // server re-opens a window (it does, since no device may be enrolled, or the previous
        // window's file is stale but readable). Either way, poll /ping until it answers.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"RankMaster2.Server exited early with code {_process.ExitCode}.");

            try
            {
                var response = await http.GetAsync(BaseUrl + "/ping").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    // The certificate may have been re-minted (e.g. certificate.pfx was deleted
                    // before this restart, § 6.3 C7) — refresh the fingerprint every other test
                    // relies on, from the public /ping subset, which always carries it (§ 14).
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("certificateFingerprint", out var fp) && fp.GetString() is { } value)
                        Fingerprint = value;
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(150).ConfigureAwait(false);
        }

        throw new TimeoutException("The server did not answer /ping within 20 s after restart.");
    }

    // ---- test conveniences -----------------------------------------------------------------------

    /// <summary>Pairs a second device against this server, playing "the phone" in lifecycle tests.
    /// Writes its own <c>pair.request</c>, reads the fresh offer, posts the code with a plain,
    /// pinned <see cref="HttpClient"/>, and returns the bearer token.</summary>
    /// <summary>
    /// Revokes a device the way the owner does: by writing <c>&lt;data&gt;/revoke.request</c>, the
    /// out-of-band channel only his OS account can reach (SERVER_SPEC.md § 10.12). Over HTTP a device
    /// may revoke only itself, so a test cannot pair a second device and unpair the first — that was
    /// the fault AUDIT2.md § 3.13 named, and closing it is why this helper exists.
    /// Returns when the server has actually acted on the file.
    /// </summary>
    public async Task RevokeDeviceAsOwnerAsync(string deviceId, TimeSpan? timeout = null)
    {
        var path = Path.Combine(DataDirectory, "revoke.request");
        await File.WriteAllTextAsync(path, deviceId).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            // The server consumes the file when it acts on it; that is the observable signal.
            if (!File.Exists(path))
                return;
            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"The server did not consume revoke.request for '{deviceId}' within the deadline.");
    }

    public async Task<SecondDevice> PairSecondDeviceAsync(string deviceName = "phone")
    {
        var offerPath = Path.Combine(DataDirectory, "pairing.json");
        var previousExpiry = ReadExpiry(offerPath);

        await File.WriteAllBytesAsync(Path.Combine(DataDirectory, "pair.request"), []).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string? code = null, fingerprint = null;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(offerPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(offerPath).ConfigureAwait(false));
                    var expiry = document.RootElement.GetProperty("expiresAt").GetString();
                    if (previousExpiry is null || expiry != previousExpiry)
                    {
                        code = document.RootElement.GetProperty("code").GetString();
                        fingerprint = document.RootElement.GetProperty("certificateFingerprint").GetString();
                        break;
                    }
                }
                catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { }
            }
            await Task.Delay(100).ConfigureAwait(false);
        }

        if (code is null || fingerprint is null)
            throw new TimeoutException("Could not obtain a fresh pairing offer for the second device.");

        using var handler = PinnedTo(fingerprint);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var body = JsonSerializer.Serialize(new { code, deviceName });
        var response = await http.PostAsync(BaseUrl + "/pair",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(json);

        return new SecondDevice(
            parsed.RootElement.GetProperty("deviceId").GetString()!,
            parsed.RootElement.GetProperty("token").GetString()!,
            fingerprint,
            BaseUrl);
    }

    private static string? ReadExpiry(string offerPath)
    {
        try
        {
            if (!File.Exists(offerPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(offerPath));
            return document.RootElement.GetProperty("expiresAt").GetString();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static SocketsHttpHandler PinnedTo(string fingerprint)
    {
        var expected = fingerprint.Trim().ToLowerInvariant();
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    var actual = "sha256:" + Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
                    return string.Equals(actual, expected, StringComparison.Ordinal);
                },
            },
        };
    }

    // ---- setup ---------------------------------------------------------------------------------

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string LocateOrBuildServerDll()
    {
        var configuration = IsDebugBuild() ? "Debug" : "Release";
        var dll = Path.Combine(Repo.ServerProjectDirectory, "bin", configuration, "net8.0", "RankMaster2.Server.dll");
        if (File.Exists(dll)) return dll;

        var start = new ProcessStartInfo(_dotnet)
        {
            WorkingDirectory = Repo.ServerProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--nologo");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var build = Process.Start(start)!;
        var output = new StringBuilder();
        output.Append(build.StandardOutput.ReadToEnd());
        output.Append(build.StandardError.ReadToEnd());
        build.WaitForExit(300_000);

        if (build.ExitCode != 0 || !File.Exists(dll))
            throw new InvalidOperationException($"Building RankMaster2.Server ({configuration}) failed (exit {build.ExitCode}):\n{output}");

        return dll;
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}

/// <summary>The token and pin a second, already-paired device needs to act as "the phone".</summary>
public sealed record SecondDevice(string DeviceId, string Token, string Fingerprint, string BaseUrl)
{
    public HttpClient CreateClient()
    {
        var handler = RealServer.PinnedTo(Fingerprint);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http;
    }
}

[CollectionDefinition("RealServer")]
public sealed class RealServerCollection : ICollectionFixture<RealServer>
{
    // Marker only. Every test class in this collection shares one RealServer and runs serially —
    // the server holds exactly one session (§ 6.2).
}
