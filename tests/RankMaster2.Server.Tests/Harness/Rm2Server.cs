using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// One in-process server for the whole suite, because the contract says there is only ever one:
/// "Exactly one session exists server-wide" (SERVER_SPEC.md § 7). Sharing a server is not an
/// optimisation here, it is the only arrangement that matches what is being tested — and it is why
/// the collection below runs its tests one at a time.
/// </summary>
public sealed class Rm2Server : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _http;

    /// <summary>
    /// A private data directory for this run: the certificate, the device store and the pairing
    /// offer live here. Giving the suite its own means it starts with no device enrolled, which is
    /// what makes a pairing window available to it at all.
    /// </summary>
    public string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "rm2-tests", $"server-data-{Guid.NewGuid():N}");

    /// <summary>A client with no bearer token, for the public and unauthenticated paths.</summary>
    public Rm2Client Anonymous { get; private set; } = null!;

    /// <summary>The working client: authenticated when a token could be had.</summary>
    public Rm2Client Client { get; private set; } = null!;

    public TestCredentials Credentials { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var seeded = TestAuth.PairingCodeConfigurationKeys
                    .ToDictionary(key => key, _ => (string?)TestAuth.SeededPairingCode);

                // Harmless if the server reads none of these; see TestAuth for why they are here.
                seeded["RankMaster2:DataDirectory"] = DataDirectory;
                seeded["Rm2:DataDirectory"] = DataDirectory;
                configuration.AddInMemoryCollection(seeded);
            });
        });

        _http = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        _http.Timeout = Rm2Client.CallTimeout;

        Anonymous = new Rm2Client(_http);
        Client = Anonymous;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Acquired lazily and once: pairing codes are single-use (§ 10.11), so this must not run per
    /// test class.
    /// </summary>
    public async Task<TestCredentials> AuthenticateAsync()
    {
        if (Credentials is not null) return Credentials;

        Credentials = await TestAuth.AcquireAsync(Anonymous, DataDirectory);
        Client = Credentials.Mode == AuthMode.Bearer ? Anonymous.WithToken(Credentials.Token) : Anonymous;
        return Credentials;
    }

    /// <summary>
    /// The authenticated client, or a failure that explains the gap rather than a bare 401.
    /// </summary>
    public async Task<Rm2Client> AuthenticatedAsync()
    {
        var credentials = await AuthenticateAsync();
        if (credentials.Mode == AuthMode.Unavailable)
            throw new Xunit.Sdk.XunitException(
                "This test needs a bearer token and none could be obtained.\n" + credentials.Diagnostic);

        return Client;
    }

    /// <summary>
    /// Close whatever session an earlier test left behind. The server holds one session and one
    /// folder lock; without this, a single failing test would cascade into every test after it.
    /// </summary>
    public async Task ResetAsync()
    {
        var credentials = await AuthenticateAsync();
        if (credentials.Mode == AuthMode.Unavailable) return;

        try
        {
            await Client.CloseSessionAsync();
        }
        catch (Xunit.Sdk.XunitException)
        {
            // A reset that cannot reach the server is the next assertion's problem to report.
        }
    }

    public Task DisposeAsync()
    {
        _http?.Dispose();
        _factory?.Dispose();

        try
        {
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a run over.
        }

        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Rm2ServerCollection : ICollectionFixture<Rm2Server>
{
    public const string Name = "rm2-server";
}

/// <summary>
/// Base class for anything that opens a session. It closes any session left over from a previous
/// test before starting and again afterwards, so one failure does not strand the folder lock.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public abstract class SessionTestBase(Rm2Server server) : IAsyncLifetime
{
    protected Rm2Server Server { get; } = server;

    protected Rm2Client Anonymous => Server.Anonymous;

    protected Task<Rm2Client> ClientAsync() => Server.AuthenticatedAsync();

    public async Task InitializeAsync() => await Server.ResetAsync();

    public async Task DisposeAsync() => await Server.ResetAsync();

    /// <summary>Open a session on a folder and return the snapshot, asserting the 201 of § 10.1.</summary>
    protected async Task<Snapshot> OpenAsync(Fixtures.LibraryFolder folder)
    {
        var client = await ClientAsync();
        var response = await client.OpenSessionAsync(folder.Path);
        return response.ShouldBeSnapshot(201, "POST /session on a rankable folder opens it (SERVER_SPEC.md § 10.1)");
    }
}
