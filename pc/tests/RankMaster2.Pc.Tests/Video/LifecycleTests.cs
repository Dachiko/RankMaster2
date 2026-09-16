namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Avalonia.Headless.XUnit;
using Xunit;

/// <summary>D-video.md § 6.1 tests 5 and 6: Play/StopAsync/Dispose semantics (§ 4.4 rules 3, 5) and
/// the two-surface cap (rule 1).</summary>
public sealed class LifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rm2-video-lifecycle-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly VideoEngine _engine;

    public LifecycleTests()
    {
        _engine = new VideoEngine(_backend, new SyncUiThread(), new VideoOptions
        {
            FirstFrameTimeout = TimeSpan.FromSeconds(5)
        });
    }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    [AvaloniaFact]
    public async Task Play_with_the_same_path_while_playing_is_a_no_op()
    {
        var path = MakeFile("a.mp4");
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);
        _backend.Players[0].RaiseFrame();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Playing);

        surface.Play(path); // same path, already playing
        await Task.Delay(50);

        Assert.Equal(1, _backend.Players[0].PlayCalls); // no second Play, no Stop
        Assert.Equal(0, _backend.Players[0].StopCalls);
    }

    [AvaloniaFact]
    public async Task Play_with_a_different_path_stops_then_opens()
    {
        var pathA = MakeFile("a.mp4");
        var pathB = MakeFile("b.mp4");
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(pathA);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);
        _backend.Players[0].RaiseFrame();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Playing);

        surface.Play(pathB);
        await TestWait.Until(() => _backend.Players[0].PlayCalls == 2);

        Assert.True(_backend.Players[0].StopCalls >= 1);
        Assert.Equal(pathB, surface.Path);
    }

    [Fact]
    public async Task StopAsync_twice_is_fine()
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(MakeFile("a.mp4"));
        await TestWait.Until(() => _backend.Players.Count == 1);

        await surface.StopAsync();
        await surface.StopAsync(); // must not throw

        Assert.Equal(VideoSurfaceState.Idle, surface.State);
    }

    [Fact]
    public async Task Dispose_twice_is_fine()
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(MakeFile("a.mp4"));
        await TestWait.Until(() => _backend.Players.Count == 1);

        surface.Dispose();
        var ex = Record.Exception(() => surface.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Play_after_Dispose_throws()
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(MakeFile("a.mp4"));
        await TestWait.Until(() => _backend.Players.Count == 1);
        surface.Dispose();

        Assert.Throws<ObjectDisposedException>(() => surface.Play(MakeFile("b.mp4")));
    }

    [Fact]
    public void A_third_Create_throws()
    {
        using var left = _engine.Create("left");
        using var right = _engine.Create("right");

        Assert.Throws<InvalidOperationException>(() => _engine.Create("third"));
    }

    [Fact]
    public void Disposing_one_surface_lets_Create_succeed_again()
    {
        var left = _engine.Create("left");
        using var right = _engine.Create("right");
        Assert.Equal(2, _engine.LiveSurfaces);

        left.Dispose();
        Assert.Equal(1, _engine.LiveSurfaces);

        using var third = _engine.Create("third");
        Assert.Equal(2, _engine.LiveSurfaces);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
