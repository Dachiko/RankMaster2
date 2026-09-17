using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RankMaster2.Catalog;
using RankMaster2.Server.Sessions;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>An <c>ICatalog</c> whose next <c>Save</c> throws once — the commit fails, the reunite
/// that follows it works. That is the shape of a disk that filled up for a moment, an antivirus
/// handle on Windows, or a USB drive pulled and pushed back.</summary>
public sealed class FailOnceCatalog : ICatalog
{
    private readonly JsonCatalog _inner = new();
    public volatile bool FailNextSave;
    public int Saves;

    public IReadOnlyList<MediaRecord> Scan(string folder) => _inner.Scan(folder);

    public void Save(string folder, IReadOnlyList<MediaRecord> records)
    {
        Interlocked.Increment(ref Saves);
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new IOException("simulated disk full during Commit");
        }

        _inner.Save(folder, records);
    }

    public IReadOnlyList<MediaRecord> RemapIds(IReadOnlyList<MediaRecord> records, IReadOnlyDictionary<MediaId, MediaId> map) =>
        _inner.RemapIds(records, map);
}

public sealed class FailOnceServer : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _http;
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "rm2-tests", $"failonce-{Guid.NewGuid():N}");
    public FailOnceCatalog Catalog { get; } = new();
    public Rm2Client Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        var registry = new SessionRegistry(catalog: Catalog);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var seeded = TestAuth.PairingCodeConfigurationKeys.ToDictionary(key => key, _ => (string?)TestAuth.SeededPairingCode);
                seeded["RankMaster2:DataDirectory"] = DataDirectory;
                seeded["Rm2:DataDirectory"] = DataDirectory;
                configuration.AddInMemoryCollection(seeded);
            });
            builder.ConfigureServices(services => services.AddSingleton(registry));
        });
        _http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _http.Timeout = Rm2Client.CallTimeout;
        var anonymous = new Rm2Client(_http);
        var credentials = await TestAuth.AcquireAsync(anonymous, DataDirectory);
        Client = credentials.Mode == AuthMode.Bearer ? anonymous.WithToken(credentials.Token) : anonymous;
    }

    public Task DisposeAsync()
    {
        _http?.Dispose();
        _factory?.Dispose();
        try { if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FailOnceCollection : ICollectionFixture<FailOnceServer>
{
    public const string Name = "rm2-failonce";
}

/// <summary>
/// AUDIT.md H2, the one that cost votes silently. After a rename whose commit save threw, the
/// ratings on disk were correct — recovery had reunited them — but the session in memory still held
/// the <i>old</i> filenames. <c>JsonCatalog.Save</c> merges by filename against what is on disk,
/// found none of them, and wrote the on-disk rows straight back: every subsequent vote answered
/// <c>200</c>, advanced <c>sessionVotes</c>, <c>cues</c> and <c>pairSeq</c> on the phone, and changed
/// nothing at all. The database was byte-identical before and after.
///
/// <para>SERVER_SPEC.md § 7.2 had promised "rename cancelled <b>or failed</b> → resynced from disk"
/// all along; only the cancel path did it (C1).</para>
/// </summary>
[Collection(FailOnceCollection.Name)]
public sealed class RenameFailurePathTests(FailOnceServer server, ITestOutputHelper output)
{
    [Fact]
    public async Task After_a_failed_commit_the_session_is_resynced_from_disk()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;
        try
        {
            var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
            var voted = (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote");

            // § 13.1: the rename flushes the write-behind before it starts, so the vote is made
            // durable here — the one-shot failure below is for the commit, which is what this test
            // is about, not for a flush that would refuse the rename before it began.
            (await client.SaveAsync()).ShouldBeSnapshot(200, "POST /session/save (SERVER_SPEC.md § 10.5)");

            server.Catalog.FailNextSave = true;   // the Commit save throws; the reunite save then works
            (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
            var final = await RenamePolling.PollToTerminalAsync(client);
            output.WriteLine("final operation: " + final.GetRawText());
            Assert.Equal("failed", final.GetProperty("state").GetString());
            Assert.True(final.GetProperty("error").GetProperty("reunited").GetBoolean(), "reunite after the failed commit");
            Assert.False(RenameEngine.JournalExists(folder.Path), "journal deleted after a successful reunite");

            var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after failure");
            output.WriteLine($"snapshot after failure: pair={string.Join("|", after.PairIds)} pairSeq={after.PairSeq} last={after.LastAction}");

            // § 7.2's row for "rename cancelled or failed", field by field.
            Assert.Equal(opened.SessionId, after.SessionId);
            Assert.Equal(voted.PairSeq + 1, after.PairSeq);
            Assert.Equal(voted.SessionVotes, after.SessionVotes);
            Assert.Equal(voted.Cues, after.Cues);
            Assert.False(after.UndoAvailable);
            Assert.Null(after.LastAction);

            foreach (var id in after.PairIds)
            {
                Assert.True(File.Exists(folder.File(id)),
                    $"SERVER_SPEC.md § 7.2: after a failed rename the session is resynced from disk; '{id}' is not on disk.");
                (await client.MetaAsync(id)).ShouldHaveStatus(200, $"media meta for the resynced id '{id}'");
            }

            // The pre-rename token belongs to a generation that is gone.
            (await client.VoteAsync(voted.RequireToken("before the rename"), "left"))
                .ShouldBeError("stale_pair_token", "SERVER_SPEC.md § 7.2: pairSeq advanced, so the old token is stale");

            // And the thing H2 was really about: a vote after the failure lands on disk. § 13.1
            // changed when it lands, not whether — POST /session/save is the contract's own way of
            // asking for "now", and every reader of rankmaster_db.json uses it.
            var dbBefore = await File.ReadAllTextAsync(Path.Combine(folder.Path, "rankmaster_db.json"));
            (await client.VoteAsync(after.RequireToken("after"), "left")).ShouldBeSnapshot(200, "vote after the failed rename");
            (await client.SaveAsync()).ShouldBeSnapshot(200, "SERVER_SPEC.md § 10.5: make the vote durable");
            var dbAfter = await File.ReadAllTextAsync(Path.Combine(folder.Path, "rankmaster_db.json"));

            Assert.True(dbBefore != dbAfter,
                "AUDIT.md H2: the vote answered 200 and rankmaster_db.json is byte-identical. The session " +
                "is holding filenames that are not on disk, so JsonCatalog.Save's merge writes the old rows " +
                "back and every vote the owner casts is thrown away without a word.");

            var loaded = new JsonCatalog().Scan(folder.Path);
            output.WriteLine("db records after vote: " + string.Join(", ", loaded.Select(r => $"{r.Filename}:{r.Matches}m/{r.Rating.Mu:F2}")));
            Assert.Contains(loaded, r => r.Matches > 0);
        }
        finally
        {
            await client.CloseSessionAsync();
        }
    }
}
