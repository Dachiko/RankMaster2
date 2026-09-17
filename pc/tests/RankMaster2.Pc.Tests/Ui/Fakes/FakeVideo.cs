using Avalonia;
using Avalonia.Media.Imaging;
using RankMaster2.Pc.Video;

namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>A scriptable <see cref="IVideoSurface"/>: a test drives it into any
/// <see cref="VideoSurfaceState"/> by calling <see cref="CompletePlay"/>/<see cref="Fail"/> after
/// <see cref="Play"/> is observed, exactly like the real engine reporting back asynchronously.</summary>
public sealed class FakeVideoSurface : IVideoSurface
{
    public string PaneName { get; }
    public VideoSurfaceState State { get; private set; } = VideoSurfaceState.Idle;
    public string? Path { get; private set; }
    public Bitmap? Frame { get; private set; }
    public PixelSize FrameSize { get; private set; }
    public VideoFailure? Failure { get; private set; }
    public VideoStats Stats { get; private set; }

    public readonly List<string> PlayCalls = new();
    public int StopCalls { get; private set; }
    public bool StopHangsForever { get; set; }

    public bool Disposed { get; private set; }
    public Action? OnDisposed { get; set; }

    public FakeVideoSurface(string paneName) => PaneName = paneName;

    public void SetPaneSize(PixelSize pixels) { }

    public void Play(string path)
    {
        if (State != VideoSurfaceState.Idle && Path == path) return; // already playing this path: no-op
        PlayCalls.Add(path);
        Path = path;
        State = VideoSurfaceState.Opening;
        Failure = null;
        StateChanged?.Invoke(this);
    }

    public Task StopAsync()
    {
        StopCalls++;
        if (StopHangsForever) return new TaskCompletionSource().Task;
        State = VideoSurfaceState.Idle;
        Frame = null;
        Path = null;
        StateChanged?.Invoke(this);
        return Task.CompletedTask;
    }

    /// <summary>Simulates the first frame arriving for whatever <see cref="Path"/> currently is.</summary>
    public void CompletePlay()
    {
        State = VideoSurfaceState.Playing;
        StateChanged?.Invoke(this);
        FrameChanged?.Invoke(this);
    }

    public void Fail(VideoFailure failure)
    {
        State = VideoSurfaceState.Failed;
        Failure = failure;
        StateChanged?.Invoke(this);
    }

    public event Action<IVideoSurface>? StateChanged;
    public event Action<IVideoSurface>? FrameChanged;

    public void Dispose()
    {
        Disposed = true;
        OnDisposed?.Invoke();
    }
}

public sealed class FakeVideoSurfaceFactory : IVideoSurfaceFactory
{
    public VideoEngineStatus EngineStatus { get; set; } = VideoEngineStatus.Ready;
    public string? EngineFailure { get; set; }
    public int LiveSurfaces { get; private set; }

    public readonly List<FakeVideoSurface> Created = new();

    public Task<VideoEngineStatus> WarmUpAsync(CancellationToken ct = default) => Task.FromResult(EngineStatus);

    public IVideoSurface Create(string paneName)
    {
        if (LiveSurfaces >= 2) throw new InvalidOperationException("FakeVideoSurfaceFactory: never a third player.");
        var surface = new FakeVideoSurface(paneName);
        surface.OnDisposed = () => LiveSurfaces--;
        Created.Add(surface);
        LiveSurfaces++;
        return surface;
    }

    public event Action<VideoEngineStatus>? EngineStatusChanged;

    public void SetEngineStatus(VideoEngineStatus status)
    {
        EngineStatus = status;
        EngineStatusChanged?.Invoke(status);
    }

    public string DiagnosticsDump() => "fake";
}
