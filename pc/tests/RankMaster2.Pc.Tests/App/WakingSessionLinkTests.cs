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
