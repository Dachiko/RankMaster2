namespace RankMaster2.Pc.Tests.App;

using RankMaster2.Pc.App;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Tests.App.Fakes;
using RankMaster2.Pc.Video;
using Xunit;

/// <summary>A-startup-and-shell.md § 8, tests 3-5a.</summary>
public sealed class WakingSessionLinkTests
{
    private static VideoEngineGate NewGate(FakeVideoSurfaceFactory engine) =>
        new(engine, nativeDir: null); // null: no Windows-style libvlc dir to index in these tests

    [Fact]
    public async Task StillsFolder_NeverWakesEngine()
    {
        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Returns(new FolderMedia(Stills: 10, Videos: 0));
        var inner = new FakeSessionLink
        {
            OnOpen = (_, _) => Task.FromResult<OpenResult>(new OpenResult.Opened(TestSnapshots.With("still"), false)),
        };

        var link = new WakingSessionLink(inner, probe, gate);

        for (var i = 0; i < 10; i++)
            await link.OpenAsync("/some/stills/folder");

        Assert.Equal(0, engine.WarmUpCalls);
    }

    [Fact]
    public async Task VideoFolder_WakesOnce_BeforeSnapshot()
    {
        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Returns(new FolderMedia(Stills: 0, Videos: 5), delay: TimeSpan.FromMilliseconds(30));

        var warmedBeforeSnapshot = false;
        var inner = new FakeSessionLink
        {
            OnOpen = async (_, _) =>
            {
                await Task.Delay(150);
                warmedBeforeSnapshot = engine.WarmUpCalls > 0;
                return new OpenResult.Opened(TestSnapshots.With("video"), false);
            },
        };

        var link = new WakingSessionLink(inner, probe, gate);
        await link.OpenAsync("/some/video/folder");

        Assert.True(warmedBeforeSnapshot, "the engine should have been warming before the snapshot arrived");
        Assert.Equal(1, engine.WarmUpCalls);

        // a second open must not warm it again
        await link.OpenAsync("/some/video/folder");
        Assert.Equal(1, engine.WarmUpCalls);
    }

    [Fact]
    public async Task ProbeWrong_SnapshotVideo_WakesLate()
    {
        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Returns(new FolderMedia(Stills: 3, Videos: 0)); // says "still"
        var inner = new FakeSessionLink
        {
            OnOpen = (_, _) => Task.FromResult<OpenResult>(new OpenResult.Opened(TestSnapshots.With("video"), false)),
        };

        var link = new WakingSessionLink(inner, probe, gate);
        await link.OpenAsync("/mixed/folder");

        // the probe's Task.Run may still be settling; give it a moment, then assert exactly once
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (engine.WarmUpCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, engine.WarmUpCalls);
    }

    /// <summary>G-audit-remediation.md § 3.7, A22: none of these three marks were ever emitted, so
    /// the startup kit could never answer "how long did connecting take". <c>tray_started</c> is
    /// conditional on <see cref="ConnectResult.Connected.StartedServer"/> — connecting to a server
    /// that was already running must not claim it started one.</summary>
    [Fact]
    public async Task ConnectAsync_MarksConnecting_Ready_AndTrayStartedWhenTrue()
    {
        StartupClock.ResetForTests();
        StartupClock.Start("test");

        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Returns(new FolderMedia(Stills: 0, Videos: 0));
        var inner = new FakeSessionLink
        {
            OnConnect = _ => Task.FromResult<ConnectResult>(
                new ConnectResult.Connected("https://127.0.0.1", Enrolled: false, StartedServer: true)),
        };

        var link = new WakingSessionLink(inner, probe, gate);
        await link.ConnectAsync();

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-waking-{Guid.NewGuid():N}.log");
        try
        {
            StartupClock.Flush(logPath);
            var parsed = StartupClock.ParseLine(File.ReadAllLines(logPath).Last());
            Assert.NotNull(parsed);
            Assert.True(parsed!.Marks.ContainsKey("link_connecting"));
            Assert.True(parsed.Marks.ContainsKey("link_ready"));
            Assert.True(parsed.Marks.ContainsKey("tray_started"));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_StartedServerFalse_NeverMarksTrayStarted()
    {
        StartupClock.ResetForTests();
        StartupClock.Start("test");

        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Returns(new FolderMedia(Stills: 0, Videos: 0));
        var inner = new FakeSessionLink
        {
            OnConnect = _ => Task.FromResult<ConnectResult>(
                new ConnectResult.Connected("https://127.0.0.1", Enrolled: false, StartedServer: false)),
        };

        var link = new WakingSessionLink(inner, probe, gate);
        await link.ConnectAsync();

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-waking-{Guid.NewGuid():N}.log");
        try
        {
            StartupClock.Flush(logPath);
            var parsed = StartupClock.ParseLine(File.ReadAllLines(logPath).Last());
            Assert.NotNull(parsed);
            Assert.True(parsed!.Marks.ContainsKey("link_ready"));
            Assert.False(parsed.Marks.ContainsKey("tray_started"));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_Failed_NeverMarksReadyOrTrayStarted()
    {
        StartupClock.ResetForTests();
        StartupClock.Start("test");

        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Returns(new FolderMedia(Stills: 0, Videos: 0));
        var inner = new FakeSessionLink
        {
            OnConnect = _ => Task.FromResult<ConnectResult>(new ConnectResult.Failed(TestSnapshots.SomeFailure)),
        };

        var link = new WakingSessionLink(inner, probe, gate);
        await link.ConnectAsync();

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-waking-{Guid.NewGuid():N}.log");
        try
        {
            StartupClock.Flush(logPath);
            var parsed = StartupClock.ParseLine(File.ReadAllLines(logPath).Last());
            Assert.NotNull(parsed);
            Assert.True(parsed!.Marks.ContainsKey("link_connecting"));
            Assert.False(parsed.Marks.ContainsKey("link_ready"));
            Assert.False(parsed.Marks.ContainsKey("tray_started"));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ProbeThrows_DecidesNothing()
    {
        var engine = new FakeVideoSurfaceFactory();
        var gate = NewGate(engine);
        var probe = FakeMediaProbe.Throws(new IOException("unreadable"));

        var inner = new FakeSessionLink
        {
            OnOpen = (_, _) => Task.FromResult<OpenResult>(new OpenResult.Opened(TestSnapshots.With("still"), false)),
        };
        var link = new WakingSessionLink(inner, probe, gate);
        await link.OpenAsync("/unreadable");
        await Task.Delay(50);
        Assert.Equal(0, engine.WarmUpCalls);

        var innerVideo = new FakeSessionLink
        {
            OnOpen = (_, _) => Task.FromResult<OpenResult>(new OpenResult.Opened(TestSnapshots.With("video"), false)),
        };
        var link2 = new WakingSessionLink(innerVideo, probe, NewGate(engine));
        await link2.OpenAsync("/unreadable");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (engine.WarmUpCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(1, engine.WarmUpCalls);
    }
}
