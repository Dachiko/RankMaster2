namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video.Backend;
using RankMaster2.Pc.Video;
using Avalonia.Headless.XUnit;
using Xunit;

/// <summary>D-video.md § 6.1 test 3: every row of § 4.5's step table, driven by <see cref="FakeBackend"/>
/// through the real <see cref="VideoEngine"/>/<see cref="VideoSurface"/> — no LibVLC.</summary>
public sealed class StateMachineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rm2-video-state-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly VideoOptions _options;
    private readonly VideoEngine _engine;

    public StateMachineTests()
    {
        _options = new VideoOptions
        {
            FirstFrameTimeout = TimeSpan.FromMilliseconds(150),
            ParseTimeout = TimeSpan.FromMilliseconds(500)
        };
        _engine = new VideoEngine(_backend, new SyncUiThread(), _options);
    }

    private string MakeFile(string name = "clip.mp4")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private async Task<VideoSurface> PlayAndWaitAsync(string path, Func<VideoSurface, bool> until)
    {
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => until(surface));
        return surface;
    }

    [Fact]
    public async Task Step1_missing_file_fails_Missing_without_calling_the_backend()
    {
        var missing = Path.Combine(_dir, "nope.mp4");
        var surface = await PlayAndWaitAsync(missing, s => s.State == VideoSurfaceState.Failed);

        Assert.Equal(VideoFailureKind.Missing, surface.Failure!.Kind);
        Assert.Empty(_backend.Players); // no player was ever created for a missing file
    }

    [Fact]
    public async Task Step2_parse_failed_fails_Unreadable()
    {
        _backend.CreatePlayerOverride = p => p.NextParseResult = ParseResult.Failed();
        var path = MakeFile();
        var surface = await PlayAndWaitAsync(path, s => s.State == VideoSurfaceState.Failed);
        Assert.Equal(VideoFailureKind.Unreadable, surface.Failure!.Kind);
    }

    [Fact]
    public async Task Step2_parse_timeout_fails_Unreadable()
    {
        _backend.CreatePlayerOverride = p => p.NextParseResult = ParseResult.TimedOut();
        var path = MakeFile();
        var surface = await PlayAndWaitAsync(path, s => s.State == VideoSurfaceState.Failed);
        Assert.Equal(VideoFailureKind.Unreadable, surface.Failure!.Kind);
    }

    [Fact]
    public async Task Step3_no_video_track_fails_NoVideoTrack()
    {
        _backend.CreatePlayerOverride = p => p.NextParseResult = ParseResult.NoVideoTrack();
        var path = MakeFile();
        var surface = await PlayAndWaitAsync(path, s => s.State == VideoSurfaceState.Failed);
        Assert.Equal(VideoFailureKind.NoVideoTrack, surface.Failure!.Kind);
    }

    [Fact]
    public async Task Step4_play_returns_false_fails_DecodeFailed()
    {
        _backend.CreatePlayerOverride = p => p.NextPlayResult = false;
        var path = MakeFile();
        var surface = await PlayAndWaitAsync(path, s => s.State == VideoSurfaceState.Failed);
        Assert.Equal(VideoFailureKind.DecodeFailed, surface.Failure!.Kind);
    }

    [Fact]
    public async Task Step5_EncounteredError_before_first_frame_fails_DecodeFailed()
    {
        var path = MakeFile();
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);

        _backend.Players[0].RaiseEncounteredError();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Failed);

        Assert.Equal(VideoFailureKind.DecodeFailed, surface.Failure!.Kind);
    }

    [Fact]
    public async Task Step6_EndReached_before_first_frame_fails_DecodeFailed_with_the_right_sentence()
    {
        var path = MakeFile();
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);

        _backend.Players[0].RaiseEndReached();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Failed);

        Assert.Equal(VideoFailureKind.DecodeFailed, surface.Failure!.Kind);
        Assert.Contains("ends before its first frame", surface.Failure!.Message);
    }

    [Fact]
    public async Task Step7_no_frame_within_timeout_fails_NoFrameInTime()
    {
        var path = MakeFile();
        var surface = await PlayAndWaitAsync(path, s => s.State == VideoSurfaceState.Failed);
        Assert.Equal(VideoFailureKind.NoFrameInTime, surface.Failure!.Kind);
    }

    [AvaloniaFact]
    public async Task Step8_first_frame_reaches_Playing_and_records_TimeToFirstFrame()
    {
        var path = MakeFile();
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);

        _backend.Players[0].RaiseFrame();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Playing);

        Assert.NotNull(surface.Frame);
        Assert.True(surface.Stats.TimeToFirstFrame >= TimeSpan.Zero);
    }

    [AvaloniaFact]
    public async Task Step9_EncounteredError_while_playing_fails_but_keeps_the_frame()
    {
        var path = MakeFile();
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);
        _backend.Players[0].RaiseFrame();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Playing);
        var frameBefore = surface.Frame;

        _backend.Players[0].RaiseEncounteredError();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Failed);

        Assert.Equal(VideoFailureKind.DecodeFailed, surface.Failure!.Kind);
        Assert.Same(frameBefore, surface.Frame); // the pane does not go black
    }

    [AvaloniaFact]
    public async Task Step10_EndReached_while_playing_restarts_and_stays_Playing()
    {
        var path = MakeFile();
        var surface = (VideoSurface)_engine.Create("left");
        surface.Play(path);
        await TestWait.Until(() => _backend.Players.Count == 1 && _backend.Players[0].PlayCalls == 1);
        _backend.Players[0].RaiseFrame();
        await TestWait.Until(() => surface.State == VideoSurfaceState.Playing);

        _backend.Players[0].RaiseEndReached();
        await TestWait.Until(() => _backend.Players[0].RestartCalls == 1);

        Assert.Equal(VideoSurfaceState.Playing, surface.State); // "state unchanged" — looping, not a failure
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
