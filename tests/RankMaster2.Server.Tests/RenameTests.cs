using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RankMaster2.Catalog;
using RankMaster2.Ranking;
using RankMaster2.Server.Sessions;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// SERVER_SPEC.md § 10.16: <c>POST /session/rename</c>, <c>GET /session/rename</c>,
/// <c>POST /session/rename/cancel</c> — the wire lifecycle of the journalled rename operation.
///
/// Six files rename in well under the time it takes to poll (§ 3.1), so the tests that need to
/// observe a non-terminal state or race a cancel against the first move use a dedicated server
/// (<see cref="RenameGateServer"/>) whose <c>ICatalog</c> and <c>IRenameJournalWriter</c> can be told
/// to block — the same seam the plan calls for, mirroring how this suite already holds a save open
/// elsewhere. Everything that does not need that determinism runs against the ordinary shared server.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public sealed class RenameTests(Rm2Server server) : SessionTestBase(server)
{
    [Fact]
    public async Task Rename_starts_with_202_and_an_operation()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);

        var client = await ClientAsync();
        var response = await client.StartRenameAsync();

        response.ShouldHaveStatus(202, "SERVER_SPEC.md § 10.16: POST /session/rename starts with 202 Accepted");
        Assert.Equal($"{Rm2Client.BasePath}/session/rename", response.HeaderOrNull("Location"));

        var body = response.JsonBody;
        Assert.Equal("running", body.GetProperty("state").GetString());
        Assert.True(body.TryGetProperty("operationId", out _));
        Assert.True(body.TryGetProperty("phase", out _));
        Assert.True(body.TryGetProperty("done", out _));
        Assert.True(body.TryGetProperty("total", out _));

        await RenamePolling.PollToTerminalAsync(client);
    }

    [Fact]
    public async Task A_succeeded_rename_renumbers_and_carries_the_ratings()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        // Vote once so at least one pair carries a non-default rating into the rename.
        var voted = (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "a vote before the rename");

        (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
        var final = await RenamePolling.PollToTerminalAsync(client);
        Assert.Equal("succeeded", final.GetProperty("state").GetString());

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "GET /session after a succeeded rename");
        Assert.Equal(voted.PairSeq + 1, after.PairSeq);
        Assert.Equal("rename", after.RequireLastAction("§ 9.4").Type);
        Assert.False(after.UndoAvailable, "SERVER_SPEC.md § 10.16: a rename clears undo.");
        Assert.NotEqual(voted.PairToken, after.PairToken);

        foreach (var id in after.PairIds)
            Assert.Matches("^\\d{6}\\.", id);

        Assert.False(
            Directory.EnumerateDirectories(folder.Path, "rankmaster_backup_*").Any(),
            "SERVER_SPEC.md § 10.16: the server's rename copies no files and creates no backup folder.");

        // The old pre-rename ids are gone.
        var staleMeta = await client.MetaAsync(opened.Left.Id);
        Assert.Equal(404, staleMeta.StatusCode);

        // A stale pre-rename token on vote is refused, not replayed against the new pair.
        var staleVote = await client.VoteAsync(voted.PairToken!, "left");
        staleVote.ShouldBeError("stale_pair_token", "a pre-rename token must not be honoured after a rename");

        // The database on disk still loads, with six records under the new names.
        var loaded = new JsonCatalog().Scan(folder.Path);
        Assert.Equal(6, loaded.Count);
        Assert.All(loaded, r => Assert.Matches("^\\d{6}\\.", r.Filename));
    }

    [Fact]
    public async Task Cancel_is_idempotent()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
        await RenamePolling.PollToTerminalAsync(client);   // a small folder finishes before cancel could matter

        var first = await client.CancelRenameAsync();
        first.ShouldHaveStatus(200, "cancel after the operation is already terminal just reports it (§ 3.5)");
        Assert.Equal("succeeded", first.JsonBody.GetProperty("state").GetString());

        var second = await client.CancelRenameAsync();
        second.ShouldHaveStatus(200, "a second cancel is idempotent");
        Assert.Equal("succeeded", second.JsonBody.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Rename_routes_need_a_session()
    {
        var client = await ClientAsync();

        (await client.StartRenameAsync()).ShouldBeError("no_session", "POST /session/rename with no session open");
        (await client.GetRenameAsync()).ShouldBeError("no_session", "GET /session/rename with no session open");
        (await client.CancelRenameAsync()).ShouldBeError("no_session", "cancel with no session open");

        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);

        // A session is open, but no rename has ever been started: the route is real (it does not
        // 404 as an unmapped path would), it just has nothing recorded yet.
        (await client.GetRenameAsync()).ShouldBeError(
            "no_rename_operation", "GET /session/rename before any rename has run");
        (await client.CancelRenameAsync()).ShouldBeError(
            "no_rename_operation", "cancel before any rename has run");
    }

    [Fact]
    public async Task Rename_takes_no_pairToken()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        // A pairToken sent in the body must be silently ignored, not validated (§ 10.16).
        var response = await client.SendAsync(
            HttpMethod.Post, "/session/rename", new { pairToken = "not-a-real-token" });

        response.ShouldHaveStatus(202, "SERVER_SPEC.md § 10.16: rename ignores a pairToken in the body");
        await RenamePolling.PollToTerminalAsync(client);
    }
}

/// <summary>
/// The three tests that need a deterministic non-terminal state: a blockable <c>ICatalog</c> (to
/// hold a run in <c>saving</c>) and a blockable <c>IRenameJournalWriter</c> (to hold a run in
/// <c>preparing</c>, before the journal exists), injected through DI exactly as
/// <c>RenameEndpoints</c>/<c>SessionEndpoints</c> already prefer a registered <c>SessionRegistry</c>
/// over <see cref="SessionRegistry.Shared"/>.
/// </summary>
[Collection(RenameGateCollection.Name)]
public sealed class RenameGatedTests(RenameGateServer server) : IAsyncLifetime
{
    private readonly RenameGateServer _server = server;

    public Task InitializeAsync() => _server.ResetAsync();
    public Task DisposeAsync() => _server.ResetAsync();

    [Fact]
    public async Task The_operation_is_observed_to_succeed()
    {
        using var folder = LibraryFolder.SixStills();
        (await _server.Client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        var gate = new TaskCompletionSource<bool>();
        _server.Catalog.SaveGate = gate;

        (await _server.Client.StartRenameAsync()).ShouldHaveStatus(202, "start");

        var observed = await RenamePolling.PollUntilAsync(
            _server.Client, op => op.GetProperty("state").GetString() != "succeeded", timeoutSeconds: 5);
        Assert.NotEqual("succeeded", observed.GetProperty("state").GetString());

        gate.SetResult(true);
        var final = await RenamePolling.PollToTerminalAsync(_server.Client);
        Assert.Equal("succeeded", final.GetProperty("state").GetString());
        Assert.Equal("done", final.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_second_rename_or_a_vote_while_one_runs_is_rename_in_progress()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = (await _server.Client.OpenSessionAsync(folder.Path))
            .ShouldBeSnapshot(201, "open");
        var token = opened.RequireToken("a fresh session is ranking");

        var gate = new TaskCompletionSource<bool>();
        _server.Catalog.SaveGate = gate;

        (await _server.Client.StartRenameAsync()).ShouldHaveStatus(202, "start");
        await RenamePolling.PollUntilAsync(
            _server.Client, op => op.GetProperty("phase").GetString() == "saving", timeoutSeconds: 5);

        (await _server.Client.StartRenameAsync()).ShouldBeError(
            "rename_in_progress", "a second POST /session/rename while one runs (§ 3.6)");

        (await _server.Client.VoteAsync(token, "left")).ShouldBeError(
            "rename_in_progress", "a mutating /session* call while a rename runs (§ 3.6)");

        gate.SetResult(true);
        var final = await RenamePolling.PollToTerminalAsync(_server.Client);
        Assert.Equal("succeeded", final.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Cancel_before_the_moves_aborts_cleanly()
    {
        using var folder = LibraryFolder.SixStills();
        (await _server.Client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");

        var gate = new TaskCompletionSource<bool>();
        _server.JournalWriter.WriteGate = gate;

        var startTask = _server.Client.StartRenameAsync();

        // Wait until the operation is published (before the journal write returns), then cancel it.
        await RenamePolling.PollUntilAsync(_server.Client, _ => true, timeoutSeconds: 5);
        (await _server.Client.CancelRenameAsync()).ShouldHaveStatus(200, "cancel while held in preparing");

        gate.SetResult(true);
        var start = await startTask;
        start.ShouldHaveStatus(202, "start still acknowledges, even though it was cancelled moments later");

        var final = await RenamePolling.PollToTerminalAsync(_server.Client);
        Assert.Equal("cancelled", final.GetProperty("state").GetString());

        Assert.False(RenameEngine.JournalExists(folder.Path), "an aborted-before-any-move cancel leaves no journal");
        foreach (var name in new[] { "alpha.jpg", "bravo.jpg", "charlie.jpg", "delta.jpg", "echo.png", "foxtrot.JPG" })
            Assert.True(folder.Has(name), $"nothing must have moved: '{name}' should still be at its original name.");
    }
}

internal static class RenamePolling
{
    public static async Task<JsonElement> PollToTerminalAsync(Rm2Client client, int timeoutSeconds = 10) =>
        await PollUntilAsync(client, op =>
        {
            var state = op.GetProperty("state").GetString();
            return state is "succeeded" or "cancelled" or "failed";
        }, timeoutSeconds);

    public static async Task<JsonElement> PollUntilAsync(
        Rm2Client client, Func<JsonElement, bool> until, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var response = await client.GetRenameAsync();

            // A 404 no_rename_operation right at the start is not a failure here: StartRenameAsync
            // publishes the operation only after acquiring the session gate, and this poll may be
            // the very first request to land, a moment before that happens.
            if (response.StatusCode == 200)
            {
                var body = response.JsonBody;
                if (until(body))
                    return body;
            }
            else if (response.StatusCode != 404)
            {
                response.ShouldHaveStatus(200, "GET /session/rename while polling");
            }

            if (DateTime.UtcNow > deadline)
                throw response.Failure($"GET /session/rename did not reach the expected state within {timeoutSeconds}s.");

            await Task.Delay(15);
        }
    }
}

// ---- the dedicated, DI-overridden server for the deterministic tests -------------------------

/// <summary><c>ICatalog</c> that can hold <c>Save</c> open on a signal, so a rename can be observed
/// mid-"saving" (pc/plans/F-server-rename.md § 6).</summary>
public sealed class GateCatalog : ICatalog
{
    private readonly JsonCatalog _inner = new();

    public volatile TaskCompletionSource<bool>? SaveGate;

    public IReadOnlyList<MediaRecord> Scan(string folder) => _inner.Scan(folder);

    public void Save(string folder, IReadOnlyList<MediaRecord> records)
    {
        SaveGate?.Task.GetAwaiter().GetResult();
        _inner.Save(folder, records);
    }

    public IReadOnlyList<MediaRecord> RemapIds(
        IReadOnlyList<MediaRecord> records, IReadOnlyDictionary<MediaId, MediaId> map) =>
        _inner.RemapIds(records, map);
}

/// <summary><c>IRenameJournalWriter</c> that can hold the journal write open on a signal, so a
/// cancel can be raced against "preparing" deterministically.</summary>
public sealed class GateJournalWriter : IRenameJournalWriter
{
    public volatile TaskCompletionSource<bool>? WriteGate;

    public void Write(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt)
    {
        WriteGate?.Task.GetAwaiter().GetResult();
        RenameEngine.WriteJournal(folder, plan, createdAt);
    }
}

/// <summary>
/// A server wired with a <see cref="SessionRegistry"/> registered through DI — which
/// <c>SessionEndpoints</c> and <c>RenameEndpoints</c> already prefer over
/// <see cref="SessionRegistry.Shared"/> — so its <see cref="GateCatalog"/> and
/// <see cref="GateJournalWriter"/> can hold a rename open at an exact step. Kept separate from the
/// suite's shared <see cref="Rm2Server"/> so nothing else in the suite is affected by the override.
/// </summary>
public sealed class RenameGateServer : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _http;

    public string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "rm2-tests", $"rename-gate-data-{Guid.NewGuid():N}");

    public GateCatalog Catalog { get; } = new();
    public GateJournalWriter JournalWriter { get; } = new();

    public Rm2Client Anonymous { get; private set; } = null!;
    public Rm2Client Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);

        var registry = new SessionRegistry(catalog: Catalog, journalWriter: JournalWriter);

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
            builder.ConfigureServices(services => services.AddSingleton(registry));
        });

        _http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _http.Timeout = Rm2Client.CallTimeout;

        Anonymous = new Rm2Client(_http);
        Client = Anonymous;

        var credentials = await TestAuth.AcquireAsync(Anonymous, DataDirectory);
        if (credentials.Mode == AuthMode.Bearer)
            Client = Anonymous.WithToken(credentials.Token);
    }

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

        Catalog.SaveGate = null;
        JournalWriter.WriteGate = null;
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
public sealed class RenameGateCollection : ICollectionFixture<RenameGateServer>
{
    public const string Name = "rm2-rename-gate-server";
}
