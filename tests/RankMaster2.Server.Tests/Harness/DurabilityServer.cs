using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RankMaster2.Server.Sessions;
using Xunit;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// A real server — real HTTP, real Kestrel pipeline, real auth — whose write-behind settings
/// (SERVER_SPEC.md § 13.1) are pinned by this fixture rather than taken from configuration.
///
/// <para>Pinned deliberately. The acceptance gate runs the whole suite twice, once with the default
/// <c>SaveDelaySeconds</c> and once with <c>0</c>, so a test that inherited the ambient setting would
/// assert something different on each run and prove neither. A test about the write-behind names the
/// mode it is testing; both modes are then exercised on both runs, and the two runs prove what they
/// are for: that everything <i>else</i> behaves identically either way.</para>
///
/// <para>The registry is registered through DI, which <c>SessionEndpoints</c>, <c>RenameEndpoints</c>
/// and <c>Rm2Host</c> already prefer over <see cref="SessionRegistry.Shared"/> — and which
/// <c>Rm2Host</c> deliberately does not reconfigure from <c>appsettings.json</c>, exactly so that a
/// fixture like this one keeps the settings it asked for.</para>
/// </summary>
public abstract class DurabilityServer : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _http;

    protected DurabilityServer(TimeSpan saveDelay, int maxUnsavedChoices)
    {
        SaveDelay = saveDelay;
        MaxUnsavedChoices = maxUnsavedChoices;
    }

    /// <summary>§ 13.1's <c>SaveDelaySeconds</c>, as a <c>TimeSpan</c> so a test need not wait one.</summary>
    public TimeSpan SaveDelay { get; }

    /// <summary>§ 13.1's <c>MaxUnsavedChoices</c>.</summary>
    public int MaxUnsavedChoices { get; }

    /// <summary>True when this server defers a plain choice; false is <c>SaveDelaySeconds = 0</c>.</summary>
    public bool DefersChoices => SaveDelay > TimeSpan.Zero;

    public string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "rm2-tests", $"durability-{Guid.NewGuid():N}");

    public Rm2Client Client { get; private set; } = null!;

    /// <summary>
    /// The registry this server serves from. Held so a test can stop the host the way a shutdown
    /// does (<c>ApplicationStopping</c> → <see cref="SessionRegistry.Dispose"/>) without tearing down
    /// the fixture every other test shares.
    /// </summary>
    public SessionRegistry Registry { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);

        Registry = new SessionRegistry(saveDelay: SaveDelay, maxUnsavedChoices: MaxUnsavedChoices);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var seeded = TestAuth.PairingCodeConfigurationKeys
                    .ToDictionary(key => key, _ => (string?)TestAuth.SeededPairingCode);
                seeded["RankMaster2:DataDirectory"] = DataDirectory;
                seeded["Rm2:DataDirectory"] = DataDirectory;
                configuration.AddInMemoryCollection(seeded);
            });
            builder.ConfigureServices(services => services.AddSingleton(Registry));
        });

        _http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _http.Timeout = Rm2Client.CallTimeout;

        var anonymous = new Rm2Client(_http);
        var credentials = await TestAuth.AcquireAsync(anonymous, DataDirectory);
        Client = credentials.Mode == AuthMode.Bearer ? anonymous.WithToken(credentials.Token) : anonymous;
    }

    /// <summary>Close whatever session the previous test left open; one server, one session (§ 7).</summary>
    public async Task ResetAsync()
    {
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
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// <c>SaveDelaySeconds = 0</c>: the escape hatch of § 13.1, and the behaviour every version of this
/// server had before it — every choice saved before its response, and a choice whose write throws
/// rolled back whole.
/// </summary>
public sealed class SaveOnEveryChoiceServer() : DurabilityServer(TimeSpan.Zero, 5);

/// <summary>
/// A write-behind whose <b>clock cannot be the cause</b>: thirty seconds is longer than any test
/// here, so the only things that write are the <c>MaxUnsavedChoices</c>-th choice and the forced
/// saves of § 13.1 — which is what makes those tests prove what they claim.
/// </summary>
public sealed class CountBoundServer() : DurabilityServer(TimeSpan.FromSeconds(30), 5);

/// <summary>
/// A write-behind whose <b>count cannot be the cause</b>: the bound under test is the clock, so
/// <c>MaxUnsavedChoices</c> is set far above anything the test casts. 200 ms rather than the
/// contract's default 2 s, because the thing being proved is "no later than the bound", not "two
/// seconds" — and a suite that waits two real seconds per timing assertion invites the flake it is
/// trying to avoid.
/// </summary>
public sealed class TimeBoundServer() : DurabilityServer(TimeSpan.FromMilliseconds(200), 1000);
