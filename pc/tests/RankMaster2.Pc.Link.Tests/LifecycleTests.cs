using System.Net.Http;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using RankMaster2.Pc.Link.Wire;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>§ 6.3 "Lifecycle" (L1-L8).</summary>
[Collection("RealServer")]
public sealed class LifecycleTests(RealServer server)
{
    [Fact]
    public async Task L1_OpenDeletesThenPosts()
    {
        using var scratch = new ScratchFolder(4);
        var (link, tap) = LinkFactory.Build(server);
        await using var linkScope = link;

        await link.ConnectAsync();
        var result = await link.OpenAsync(scratch.Path);

        var opened = Assert.IsType<OpenResult.Opened>(result);
        Assert.False(opened.ReplacedOther);
        Assert.Equal(LinkState.InSession, link.State);

        var sessionCalls = tap.Sent.Where(s => s.Path == "/session").ToList();
        Assert.Equal(2, sessionCalls.Count);
        Assert.Equal("DELETE", sessionCalls[0].Method);
        Assert.Equal("POST", sessionCalls[1].Method);
    }

    [Fact]
    public async Task L2_ReplacesAnotherDevicesOpenFolder()
    {
        using var scratchX = new ScratchFolder(4);
        using var scratchY = new ScratchFolder(4);

        var phone = await server.PairSecondDeviceAsync("l2-phone");
        using var phoneHttp = phone.CreateClient();
        await phoneHttp.DeleteAsync(server.BaseUrl + "/session");
        var openX = await phoneHttp.PostAsync(server.BaseUrl + "/session",
            JsonBody($"{{\"folder\":{System.Text.Json.JsonSerializer.Serialize(scratchX.Path)}}}"));
        openX.EnsureSuccessStatusCode();

        var (link, _) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();
        var result = await link.OpenAsync(scratchY.Path);

        var opened = Assert.IsType<OpenResult.Opened>(result);
        Assert.True(opened.ReplacedOther);
        Assert.Equal(scratchY.Path, opened.Snapshot.Folder);

        var phoneSession = await phoneHttp.GetAsync(server.BaseUrl + "/session");
        var body = await phoneSession.Content.ReadAsStringAsync();
        Assert.Contains(scratchY.Path.Replace("\\", "\\\\"), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task L3_FolderFailuresMapToTheRightKindAndTouchNothing()
    {
        // folder_not_found
        {
            var (link, _) = LinkFactory.Build(server);
            await using var linkScope = link;
            await link.ConnectAsync();
            var missing = Path.Combine(Path.GetTempPath(), "rm2-does-not-exist-" + Guid.NewGuid().ToString("N"));
            var result = await link.OpenAsync(missing);
            Assert.Equal(FailureKind.FolderNotFound, Assert.IsType<OpenResult.Failed>(result).Failure.Kind);
        }

        // folder_not_a_directory
        {
            var file = Path.GetTempFileName();
            try
            {
                var (link, _) = LinkFactory.Build(server);
                await using var linkScope = link;
                await link.ConnectAsync();
                var result = await link.OpenAsync(file);
                Assert.Equal(FailureKind.FolderNotADirectory, Assert.IsType<OpenResult.Failed>(result).Failure.Kind);
            }
            finally { File.Delete(file); }
        }

        // folder_access_denied
        {
            var denied = Path.Combine(Path.GetTempPath(), "rm2-denied-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(denied);
            File.SetUnixFileMode(denied, UnixFileMode.None);
            try
            {
                var (link, _) = LinkFactory.Build(server);
                await using var linkScope = link;
                await link.ConnectAsync();
                var result = await link.OpenAsync(denied);
                Assert.Equal(FailureKind.FolderAccessDenied, Assert.IsType<OpenResult.Failed>(result).Failure.Kind);
            }
            finally
            {
                File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(denied, recursive: true);
            }
        }

        // folder_not_rankable
        {
            using var scratch = new ScratchFolder(1);
            var (link, _) = LinkFactory.Build(server);
            await using var linkScope = link;
            await link.ConnectAsync();
            var result = await link.OpenAsync(scratch.Path);
            Assert.Equal(FailureKind.FolderNotRankable, Assert.IsType<OpenResult.Failed>(result).Failure.Kind);
        }

        // library_json_unreadable — the file must not be touched
        {
            using var scratch = new ScratchFolder(4);
            var dbPath = Path.Combine(scratch.Path, "rankmaster_db.json");
            File.WriteAllText(dbPath, "{");
            var before = File.ReadAllBytes(dbPath);

            var (link, _) = LinkFactory.Build(server);
            await using var linkScope = link;
            await link.ConnectAsync();
            var result = await link.OpenAsync(scratch.Path);
            Assert.Equal(FailureKind.LibraryJsonUnreadable, Assert.IsType<OpenResult.Failed>(result).Failure.Kind);

            var after = File.ReadAllBytes(dbPath);
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task L4_FolderLockedThenReleased()
    {
        using var scratch = new ScratchFolder(4);
        var lockPath = Path.Combine(scratch.Path, ".rankmaster.lock");
        using (var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var (link, _) = LinkFactory.Build(server);
            await using var linkScope = link;
            await link.ConnectAsync();
            var result = await link.OpenAsync(scratch.Path);
            Assert.Equal(FailureKind.FolderLocked, Assert.IsType<OpenResult.Failed>(result).Failure.Kind);
        }

        {
            var (link, _) = LinkFactory.Build(server);
            await using var linkScope = link;
            await link.ConnectAsync();
            var result = await link.OpenAsync(scratch.Path);
            Assert.IsType<OpenResult.Opened>(result);
        }
    }

    [Fact]
    public async Task L5_CloseTwiceBothReturnNormally()
    {
        using var scratch = new ScratchFolder(4);
        var (link, _) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();
        await link.OpenAsync(scratch.Path);

        await link.CloseAsync();
        Assert.Null(link.LastFailure);

        await link.CloseAsync();
        Assert.Null(link.LastFailure);
    }

    [Fact]
    public async Task L6_ReopeningTheSameFolderStartsFresh()
    {
        using var scratch = new ScratchFolder(4);
        var (link, _) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();

        var first = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var voted = Assert.IsType<ActionResult.Applied>(await link.VoteAsync(Side.Left, first.Snapshot.PairSeq));
        Assert.Equal(1, voted.Snapshot.SessionVotes);

        // No CloseAsync here: simulates a crash and restart of the client without a clean close.
        var second = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        Assert.Equal(0, second.Snapshot.SessionVotes);
        Assert.NotEqual(voted.Snapshot.SessionId, second.Snapshot.SessionId);
    }

    [Fact]
    public async Task L7_ServerKilledMidSessionRefreshReconnectsAndReopens()
    {
        using var scratch = new ScratchFolder(4);

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

        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var oldSessionId = opened.Snapshot.SessionId;

        server.Kill();

        var refreshed = await link.RefreshAsync();

        if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid))
            server.AdoptPid(pid);

        var resynced = Assert.IsType<ActionResult.Resynchronised>(refreshed);
        Assert.Equal(ResyncReason.SessionReplaced, resynced.Why);
        Assert.NotEqual(oldSessionId, resynced.Snapshot.SessionId);
    }

    [Fact]
    public async Task L8_EscTimesOutQuicklyAndNeverThrows()
    {
        using var scratch = new ScratchFolder(4);
        var (link, tap) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();
        await link.OpenAsync(scratch.Path);

        tap.Script(HttpMethod.Delete, "/session", ScriptedFault.Delay(2000));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await link.CloseAsync(cts.Token);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1500,
            $"CloseAsync took {stopwatch.ElapsedMilliseconds}ms, expected to return promptly after the 500ms token fired.");
    }

    private static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
