// THROWAWAY AUDIT TESTS — evidence for AUDIT.md, not part of the suite's contract. Each test asserts
// what SERVER_SPEC.md / openapi.yaml promise; a failure documents a deviation, it is not a bug in
// the test. Delete freely.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

[Collection(Rm2ServerCollection.Name)]
public sealed class Audit_ContractDrift(Rm2Server server, ITestOutputHelper output) : SessionTestBase(server)
{
    // (11c) discard -> vote -> drop_missing -> undo. § 10.10: one level; drop_missing is not
    // cancellable; the vote before it can no longer be cancelled (Drop cleared the engine snapshot).
    // Spec-conformant answer for the undo is therefore 409 nothing_to_undo.
    [Fact]
    public async Task Audit_undo_after_drop_missing_does_not_reach_back_to_an_older_discard()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var discardedId = opened.Left.Id;
        var afterDiscard = (await client.DiscardAsync(opened.RequireToken("open"), "left", "d1"))
            .ShouldBeSnapshot(200, "discard");
        Assert.True(afterDiscard.UndoAvailable);
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", discardedId)));

        var afterVote = (await client.VoteAsync(afterDiscard.RequireToken("after discard"), "left", "v1"))
            .ShouldBeSnapshot(200, "vote");
        Assert.True(afterVote.UndoAvailable, "the vote is cancellable");

        // Delete the current left file out from under the session, then discard that side.
        var vanished = afterVote.Left.Id;
        File.Delete(folder.File(vanished));
        var afterDrop = (await client.DiscardAsync(afterVote.RequireToken("after vote"), "left", "d2"))
            .ShouldBeSnapshot(200, "discard of a vanished file");
        Assert.Equal("drop_missing", afterDrop.RequireLastAction("drop").Type);
        output.WriteLine($"after drop_missing: undoAvailable={afterDrop.UndoAvailable}");

        var undo = await client.UndoAsync("u1");
        output.WriteLine($"undo -> {undo.StatusCode} {undo.ErrorCode ?? "ok"}; body: {undo.Text[..Math.Min(400, undo.Text.Length)]}");
        var discardedStillInDiscarded = File.Exists(Path.Combine(folder.Path, "discarded", discardedId));
        output.WriteLine($"first-discarded file still in discarded/: {discardedStillInDiscarded}");

        undo.ShouldBeError("nothing_to_undo",
            "SERVER_SPEC.md § 10.10: one level; drop_missing is not cancellable and the vote before it " +
            "is no longer cancellable, so nothing older may be reached");
    }

    // § 4: error.session present iff a session is open and the caller is authenticated. Media
    // errors are produced while a session is open; does the media layer honour the iff?
    [Fact]
    public async Task Audit_media_errors_carry_error_session_while_a_session_is_open()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        var missing = await client.MetaAsync("no-such-file.jpg");
        var failure = missing.ShouldBeError("unknown_media_id", "unknown id");
        output.WriteLine("media 404 body: " + missing.Text);
        Assert.NotNull(failure.Session);
    }

    // openapi.yaml: POST /session/rename lists 400 BadRequest and a RenameStartRequest schema.
    // The server does not read the body at all.
    [Fact]
    public async Task Audit_rename_start_with_malformed_json_body()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/session/rename", "{ not json");
        output.WriteLine($"POST /session/rename with malformed JSON -> {response.StatusCode} {response.ErrorCode}");
        if (response.StatusCode == 202)
            await RenamePolling.PollToTerminalAsync(client);
        Assert.Equal(400, response.StatusCode);
    }

    // 405 is not in § 6. What does a wrong method produce?
    [Fact]
    public async Task Audit_wrong_method_on_a_real_route()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Put, "/session", new { folder = "/x" });
        output.WriteLine($"PUT /session -> {response.StatusCode} {response.ErrorCode}; Allow={response.HeaderOrNull("Allow")}");
        output.WriteLine("body: " + response.Text);
        Assert.Equal("not_found", response.ErrorCode);
    }

    // Two envelope writers: does one emit "details": null / "session": null and the other omit them?
    [Fact]
    public async Task Audit_envelope_null_fields_are_consistent_between_layers()
    {
        var client = await ClientAsync();
        var noSession = await client.GetSessionAsync();          // session layer
        var routeMiss = await client.GetAsync("/nope");          // security layer
        output.WriteLine("session layer: " + noSession.Text);
        output.WriteLine("security layer: " + routeMiss.Text);
        var a = noSession.JsonBody.GetProperty("error").TryGetProperty("details", out _);
        var b = routeMiss.JsonBody.GetProperty("error").TryGetProperty("details", out _);
        Assert.Equal(a, b);
    }

    // § 12.4: 416 carries Content-Range and the envelope. Does it also carry the byte-cache
    // headers that were applied before the range was parsed?
    [Fact]
    public async Task Audit_416_headers()
    {
        using var folder = LibraryFolder.VideosOnly();
        await OpenAsync(folder);
        var client = await ClientAsync();
        var r = await client.VideoAsync(MediaFixtures.Video, range: "bytes=999999999-");
        r.ShouldBeError("range_not_satisfiable", "416");
        output.WriteLine($"416 headers: ETag={r.HeaderOrNull("ETag")} Cache-Control={r.HeaderOrNull("Cache-Control")} Accept-Ranges={r.HeaderOrNull("Accept-Ranges")} Content-Range={r.HeaderOrNull("Content-Range")}");
        Assert.Null(r.HeaderOrNull("ETag"));
    }

    // § 10.5 says save answers save_failed with recordsChanged:false; § 9.1 lastSavedAt. Also:
    // a discard whose source vanished but whose Save also fails - what does the snapshot say?
    // (drop_missing + save failure is unspecified; just record it.)
    [Fact]
    public async Task Audit_head_on_a_cold_still_renders_it()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var head = await client.HeadAsync(opened.Left.StillLink!);
        output.WriteLine($"HEAD cold still -> {head.StatusCode} Content-Length={head.HeaderOrNull("Content-Length")}");
        Assert.Equal(200, head.StatusCode);
    }

    // § 7.2 / § 10.16: "rename cancelled or failed -> resynced from disk". This is the cancel path.
    [Fact]
    public async Task Audit_rename_in_exhausted_state_is_accepted()
    {
        using var folder = LibraryFolder.TwoStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var d = (await client.DiscardAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "discard");
        Assert.True(d.IsExhausted);
        var r = await client.StartRenameAsync();
        output.WriteLine($"rename while exhausted -> {r.StatusCode} {r.ErrorCode}");
        if (r.StatusCode == 202)
        {
            var final = await RenamePolling.PollToTerminalAsync(client);
            output.WriteLine("final: " + final.GetRawText());
            var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after");
            output.WriteLine($"after: state={after.State} total={after.Total} pairSeq={after.PairSeq} last={after.LastAction}");
        }
    }
}

// ---- (12) rename FAILURE path: a catalog that throws once during Commit --------------------

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
            throw new IOException("audit: simulated disk full during Commit");
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
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "rm2-tests", $"audit-failonce-{Guid.NewGuid():N}");
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
    public const string Name = "rm2-audit-failonce";
}

[Collection(FailOnceCollection.Name)]
public sealed class Audit_RenameFailurePath(FailOnceServer server, ITestOutputHelper output)
{
    [Fact]
    public async Task Audit_after_a_failed_commit_the_session_is_resynced_from_disk()
    {
        using var folder = LibraryFolder.SixStills();
        var client = server.Client;
        try
        {
            var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
            var voted = (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote");

            server.Catalog.FailNextSave = true;   // the Commit save throws; the reunite save then works
            (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
            var final = await RenamePolling.PollToTerminalAsync(client);
            output.WriteLine("final operation: " + final.GetRawText());
            Assert.Equal("failed", final.GetProperty("state").GetString());
            Assert.True(final.GetProperty("error").GetProperty("reunited").GetBoolean(), "reunite after the failed commit");
            Assert.False(RenameEngine.JournalExists(folder.Path), "journal deleted after a successful reunite");

            var onDisk = Directory.GetFiles(folder.Path).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            output.WriteLine("on disk: " + string.Join(", ", onDisk));

            var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after failure");
            output.WriteLine($"snapshot after failure: pair={string.Join("|", after.PairIds)} pairSeq={after.PairSeq} last={after.LastAction}");
            foreach (var id in after.PairIds)
                output.WriteLine($"  media meta for '{id}': {(await client.MetaAsync(id)).StatusCode}");

            // Then a vote in this state: does it land on disk?
            var dbBefore = File.ReadAllText(Path.Combine(folder.Path, "rankmaster_db.json"));
            var v2 = await client.VoteAsync(after.RequireToken("after"), "left");
            output.WriteLine($"vote after failed rename -> {v2.StatusCode} {v2.ErrorCode}");
            var dbAfter = File.ReadAllText(Path.Combine(folder.Path, "rankmaster_db.json"));
            var loaded = new JsonCatalog().Scan(folder.Path);
            output.WriteLine("db records after vote: " + string.Join(", ", loaded.Select(r => $"{r.Filename}:{r.Matches}m/{r.Rating.Mu:F2}")));

            // § 7.2: the session is resynced from disk, so every id in the pair must exist on disk.
            foreach (var id in after.PairIds)
                Assert.True(File.Exists(folder.File(id)),
                    $"SERVER_SPEC.md § 7.2: after a failed rename the session is resynced from disk; '{id}' is not on disk.");
        }
        finally
        {
            await client.CloseSessionAsync();
        }
    }
}
