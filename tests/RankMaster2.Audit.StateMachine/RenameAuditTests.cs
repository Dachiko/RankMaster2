using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RankMaster2.Audit.StateMachine.Harness;
using RankMaster2.Server.Sessions;
using RankMaster2.Server.Tests;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.StateMachine;

/// <summary>
/// pc/plans/F-server-rename.md § 6, the state-machine audit items for rename: a jammed save proves
/// the failure path still reunites the ratings, a rename really does hold the session against other
/// mutations, and a rename clears the one-level undo the way any other structural change does.
/// </summary>
public sealed class RenameAuditTests(Rm2Server server) : AuditTestBase(server)
{
    /// <summary>
    /// <c>AuditFolder.JamSave()</c> puts a directory where <c>rankmaster_db.json.tmp</c> needs to be,
    /// so every <c>JsonCatalog.Save</c> throws — including the commit's save AND the reunite save
    /// recovery attempts, so this exercises the true double-fault: <c>rename_failed</c> with
    /// <c>reunited: false</c> and the journal left behind. Unjamming and reopening the folder must
    /// then reunite every rating, because recovery runs again before anything scans.
    /// </summary>
    [Fact]
    public async Task RenameFailureReunitesAndKeepsRatings()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var voted = (await client.VoteAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "a vote before the rename, so there is a real rating to lose");
        Assert.Equal(1, voted.SessionVotes);

        folder.JamSave();
        try
        {
            var start = await client.StartRenameAsync();
            start.ShouldHaveStatus(202, "POST /session/rename (SERVER_SPEC.md § 10.16)");

            var terminal = await PollToTerminalAsync(client);
            Assert.Equal("failed", terminal.GetProperty("state").GetString());

            var error = terminal.GetProperty("error");
            Assert.Equal("rename_failed", error.GetProperty("code").GetString());
            Assert.False(error.GetProperty("reunited").GetBoolean(),
                "SERVER_SPEC.md § 5.7: the reunite Save itself failed too (still jammed), so this is " +
                "the rare double fault and reunited must say so truthfully.");
            Assert.False(string.IsNullOrEmpty(error.GetProperty("journal").GetString()),
                "SERVER_SPEC.md § 5.7: reunited: false must name the journal left for the next open to retry.");

            // SERVER_SPEC.md § 7.2: a rename that failed resyncs the session exactly as a cancel
            // does — pairSeq +1 because the generation genuinely changed, sessionVotes kept because
            // the owner really did cast them. The failure path used to do neither: it cleared the
            // flag and left the session holding filenames that were no longer on disk, so every
            // later vote answered 200 and wrote nothing (AUDIT.md H2, A1).
            var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "GET /session after the failure");
            Assert.Equal(voted.PairSeq + 1, after.PairSeq);
            Assert.Equal(voted.SessionVotes, after.SessionVotes);
            Assert.Equal(voted.Cues, after.Cues);
            Assert.False(after.UndoAvailable);
            Assert.Null(after.LastAction);

            foreach (var id in after.PairIds)
            {
                Assert.True(File.Exists(Path.Combine(folder.Path, id)),
                    $"the session is resynced from disk, so '{id}' must be a file that is there.");
            }
        }
        finally
        {
            folder.UnjamSave();
        }

        // The next open runs recovery before anything scans (SERVER_SPEC.md § 10.16). This time the
        // reunite save can actually write, so every rating must come back under its current name.
        await Server.ResetAsync();
        var reopened = (await client.OpenSessionAsync(folder.Path))
            .ShouldBeSnapshot(201, "reopening after the jam clears runs recovery and opens cleanly");

        Assert.Equal(6, reopened.Total);
        var metaOfWinner = await client.MetaAsync(reopened.PairIds[0]);
        metaOfWinner.ShouldHaveStatus(200, "a record from the reunited database is readable");
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.16 concurrency: while a rename runs, every other mutating call answers
    /// <c>409 rename_in_progress</c>. Held deterministically in the "saving" phase with a blockable
    /// <c>ICatalog</c> — the same seam <c>RenameTests</c> uses — because a small folder's rename over
    /// real HTTP otherwise finishes before a second request could reliably land.
    /// </summary>
    [Fact]
    public async Task RenameHoldsTheSessionAgainstOtherActions()
    {
        using var folder = AuditFolder.SixStills();
        var catalog = new GateCatalog();
        var registry = new SessionRegistry(catalog: catalog);
        var dataDirectory = Path.Combine(Path.GetTempPath(), "rm2-audit", $"rename-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
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
            http.Timeout = Rm2Client.CallTimeout;
            var anonymous = new Rm2Client(http);

            var credentials = await TestAuth.AcquireAsync(anonymous, dataDirectory);
            var client = credentials.Mode == AuthMode.Bearer ? anonymous.WithToken(credentials.Token) : anonymous;

            var opened = (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open");
            var token = opened.RequireToken("a fresh session is ranking");

            var gate = new TaskCompletionSource<bool>();
            catalog.SaveGate = gate;

            (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
            await PollUntilAsync(client, op => op.GetProperty("phase").GetString() == "saving", timeoutSeconds: 5);

            var vote = await client.VoteAsync(token, "left");
            vote.ShouldBeError("rename_in_progress",
                "SERVER_SPEC.md § 10.16 § 3.6: a mutating /session* call while a rename runs");

            var discard = await client.DiscardAsync(token, "left");
            discard.ShouldBeError("rename_in_progress", "discard is a mutating call too");

            // Reads are unaffected.
            (await client.GetSessionAsync()).ShouldBeSnapshot(200, "GET /session still works while a rename runs");

            gate.SetResult(true);
            var terminal = await PollToTerminalAsync(client);
            Assert.Equal("succeeded", terminal.GetProperty("state").GetString());
        }
        finally
        {
            try { if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true); }
            catch (IOException) { /* not worth failing the run over */ }
        }
    }

    [Fact]
    public async Task RenameClearsUndo()
    {
        using var folder = AuditFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var discarded = (await client.DiscardAsync(opened.RequireToken("ranking"), "left"))
            .ShouldBeSnapshot(200, "a discard leaves an undo offer");
        Assert.True(discarded.UndoAvailable);

        (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
        await PollToTerminalAsync(client);

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "GET /session after the rename");
        Assert.False(after.UndoAvailable,
            "SERVER_SPEC.md § 10.16: a rename clears undo — the discarded file's old name no longer " +
            "exists to be moved back to.");

        var undo = await client.UndoAsync();
        undo.ShouldBeError("nothing_to_undo", "there is nothing left to undo after a rename");
    }

    // ---- polling helpers (mirrors RankMaster2.Server.Tests.RenamePolling, which is internal) ----

    private static async Task<JsonElement> PollToTerminalAsync(Rm2Client client, int timeoutSeconds = 10) =>
        await PollUntilAsync(client, op =>
        {
            var state = op.GetProperty("state").GetString();
            return state is "succeeded" or "cancelled" or "failed";
        }, timeoutSeconds);

    private static async Task<JsonElement> PollUntilAsync(
        Rm2Client client, Func<JsonElement, bool> until, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var response = await client.GetRenameAsync();
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
