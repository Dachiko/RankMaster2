namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;
using Xunit;

/// <summary>D-video.md § 6.1 test 4: an event armed under generation N, arriving after
/// Play/StopAsync/Dispose bumped the surface to N+1, changes nothing (§ 4.4 rule 2).</summary>
public sealed class GenerationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rm2-video-gen-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly VideoEngine _engine;

    public GenerationTests()
    {
        _engine = new VideoEngine(_backend, new SyncUiThread(), new VideoOptions
        {
            FirstFrameTimeout = TimeSpan.FromSeconds(5) // long enough that it never fires mid-test
        });
    }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    [Fact]
    public async Task A_stale_generations_OnDisplay_does_not_move_the_surface_to_Playing()
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(MakeFile("a.mp4"));
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);

        var staleGeneration = surface.CurrentGeneration; // the generation this Play armed
        await surface.StopAsync(); // bumps the generation past staleGeneration

        var callbacks = (IPlayerCallbacks)surface;
        callbacks.OnFormat(1920, 1080, staleGeneration);
        callbacks.OnDisplay(staleGeneration); // a late callback from the superseded generation

        Assert.Equal(VideoSurfaceState.Idle, surface.State); // unmoved by the stale callback
        Assert.Null(surface.Frame);
    }

    [Fact]
    public async Task A_stale_generations_EncounteredError_does_not_fail_the_new_play()
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(MakeFile("a.mp4"));
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);
        var staleGeneration = surface.CurrentGeneration;

        surface.Play(MakeFile("b.mp4")); // a different path: StopAsync then a new generation
        await TestWait.Until(() => _backend.Players[0].PlayCalls == 2);

        var callbacks = (IPlayerCallbacks)surface;
        callbacks.OnEncounteredError(staleGeneration);
        await Task.Delay(50); // give a wrongly-accepted callback time to land

        Assert.NotEqual(VideoFailureKind.DecodeFailed, surface.Failure?.Kind);
        Assert.NotEqual(VideoSurfaceState.Failed, surface.State);
    }

    [Fact]
    public async Task A_stale_generations_EndReached_does_not_restart_the_new_play()
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(MakeFile("a.mp4"));
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);
        var staleGeneration = surface.CurrentGeneration;

        await surface.StopAsync();

        var callbacks = (IPlayerCallbacks)surface;
        callbacks.OnEndReached(staleGeneration);
        await Task.Delay(50);

        Assert.Equal(0, _backend.Players[0].RestartCalls);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
