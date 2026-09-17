using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RankMaster2.Server.Sessions;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>
/// What a vote costs, over HTTP, with and without the bounded write-behind of SERVER_SPEC.md § 13.1
/// — the number the owner traded a promise for (AUDIT.md A2, and his ruling of 2026-09-17:
/// <i>"I'm not afraid of losing a couple of votes, it's non-consequential"</i>).
///
/// <para>It drives 100 real <c>POST /session/vote</c> calls against a real server on a real library,
/// at the two library sizes <c>SPEC.md</c> talks about (2 000 and 20 000 files), with
/// <c>SaveDelaySeconds = 0</c> and with the default 2, on tmpfs and on this box's real disk. It
/// prints mean and maximum per-vote latency and total wall time.</para>
///
/// <para><b>It prints; it never asserts a time.</b> A wall-clock threshold on a shared build box
/// fails for reasons that have nothing to do with the product, and a test that fails for the wrong
/// reason is worse than no test — the same rule <c>SaveCostBenchmark</c> follows. What it does assert
/// is that the votes were actually applied, so a fast number cannot come from a server that did
/// nothing. Run it on its own with
/// <c>dotnet test --filter Category=Benchmark -l "console;verbosity=detailed"</c>.</para>
///
/// <para>The one cost it cannot measure is the owner's: an fsync on a USB drive. Only his machine
/// can show that, and § 6 of the remediation plan says so.</para>
/// </summary>
[Collection(VoteCostCollection.Name)]
public sealed class VoteCostBenchmark(ITestOutputHelper output)
{
    private const int Votes = 100;

    /// <summary>
    /// Where a library of this run lives. tmpfs is the fast case and the one every other test here
    /// uses; the real disk is the honest one, and on this box they are different filesystems.
    /// </summary>
    private static string RootFor(bool tmpfs)
    {
        if (tmpfs)
            return Path.Combine(Path.GetTempPath(), "rm2-bench");

        // Not the repository, and not /tmp: somewhere on the machine's actual disk. XDG_CACHE_HOME
        // if it is set and not itself a temp filesystem, else ~/.cache.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "rm2-bench");
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(2_000, true)]
    [InlineData(2_000, false)]
    [InlineData(20_000, true)]
    [InlineData(20_000, false)]
    public async Task Cost_of_a_vote_with_and_without_the_write_behind(int files, bool tmpfs)
    {
        var where = tmpfs ? "tmpfs" : "disk";

        foreach (var delay in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2) })
        {
            var root = Path.Combine(RootFor(tmpfs), $"n{files}-{(delay == TimeSpan.Zero ? "sync" : "behind")}-{Guid.NewGuid():N}");
            var dataDirectory = Path.Combine(RootFor(true), $"data-{Guid.NewGuid():N}");

            try
            {
                BuildLibrary(root, files);
                Directory.CreateDirectory(dataDirectory);

                var registry = new SessionRegistry(saveDelay: delay, maxUnsavedChoices: 5);
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        var seeded = TestAuth.PairingCodeConfigurationKeys
                            .ToDictionary(key => key, _ => (string?)TestAuth.SeededPairingCode);
                        seeded["RankMaster2:DataDirectory"] = dataDirectory;
                        seeded["Rm2:DataDirectory"] = dataDirectory;
                        configuration.AddInMemoryCollection(seeded);
                    });
                    builder.ConfigureServices(services => services.AddSingleton(registry));
                });

                using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                http.Timeout = TimeSpan.FromMinutes(5);
                var anonymous = new Rm2Client(http);
                var credentials = await TestAuth.AcquireAsync(anonymous, dataDirectory);
                var client = credentials.Mode == AuthMode.Bearer ? anonymous.WithToken(credentials.Token) : anonymous;

                var snapshot = (await client.OpenSessionAsync(root)).ShouldBeSnapshot(201, "open the benchmark library");

                var latencies = new double[Votes];
                var total = Stopwatch.StartNew();
                for (var i = 0; i < Votes; i++)
                {
                    var one = Stopwatch.StartNew();
                    var response = await client.VoteAsync(snapshot.RequireToken("ranking"), i % 2 == 0 ? "left" : "right");
                    one.Stop();
                    latencies[i] = one.Elapsed.TotalMilliseconds;
                    snapshot = response.ShouldBeSnapshot(200, $"vote {i + 1}");
                }
                total.Stop();

                // The votes were real. A benchmark that measured a server doing nothing would be
                // worse than no benchmark, and this is the cheapest possible guard against it.
                Assert.Equal(Votes, snapshot.SessionVotes);

                // Whatever the mode, everything is on disk once the client asks (§ 10.5).
                var saveCost = Stopwatch.StartNew();
                (await client.SaveAsync()).ShouldBeSnapshot(200, "the durability point after the run");
                saveCost.Stop();

                Array.Sort(latencies);
                var mean = latencies.Average();
                var max = latencies[^1];
                var median = latencies[Votes / 2];

                output.WriteLine(
                    $"n={files,-6} {where,-6} SaveDelaySeconds={(delay == TimeSpan.Zero ? "0" : "2")}  " +
                    $"mean {mean,7:0.0} ms  median {median,7:0.0} ms  max {max,7:0.0} ms  " +
                    $"{Votes} votes in {total.Elapsed.TotalSeconds,6:0.00} s  " +
                    $"(final POST /session/save {saveCost.Elapsed.TotalMilliseconds:0} ms)");

                await client.CloseSessionAsync();
            }
            finally
            {
                Delete(root);
                Delete(dataDirectory);
            }
        }
    }

    /// <summary>
    /// A library of <paramref name="files"/> real, tiny JPEGs. They are never decoded here — a vote
    /// touches the database and the filesystem listing, which is the cost being measured — but they
    /// must be real enough for <c>JsonCatalog.Scan</c> to list them and for the snapshot to stat them.
    /// </summary>
    private static void BuildLibrary(string root, int files)
    {
        Directory.CreateDirectory(root);
        var bytes = MediaFixtures.Jpeg(16, 16);
        for (var i = 0; i < files; i++)
            File.WriteAllBytes(Path.Combine(root, $"IMG_{i:D6}.jpg"), bytes);
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// On its own, and one at a time: it builds twenty thousand files and then hammers one server, so
/// running it beside the timing-sensitive tests would make both of them liars.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VoteCostCollection
{
    public const string Name = "rm2-vote-cost";
}
