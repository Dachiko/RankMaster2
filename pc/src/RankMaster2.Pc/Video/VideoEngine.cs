namespace RankMaster2.Pc.Video;

using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using RankMaster2.Pc.Video.Backend;

/// <summary>
/// <see cref="IVideoSurfaceFactory"/>: the process-wide engine. The constructor does no native work —
/// D-video.md § 4.2's laziness rule — and everything that reaches <see cref="IPlayerBackend"/> runs on
/// one dedicated worker thread, created only by the first <see cref="WarmUpAsync"/> (D-video.md § 4.6).
/// Also runs the alternate-mode coordinator of § 4.3 over its live surfaces.
/// </summary>
public sealed class VideoEngine : IVideoSurfaceFactory, IDisposable
{
    private readonly object _gate = new();
    private readonly IPlayerBackend _backend;
    private readonly IUiThread _ui;
    private readonly VideoOptions _options;
    private readonly string? _nativeDirectory;
    private readonly VideoLog _log;
    private readonly LoadWatch _loadWatch;
    private readonly List<VideoSurface> _live = new();

    private Task<VideoEngineStatus>? _warmUpTask;
    private Thread? _worker;
    private BlockingCollection<Action>? _queue;
    private Timer? _loadWatchTimer;
    private bool _disposed;

    /// <summary>Stores options; touches nothing native. D-video.md § 4.2: "the constructor does no
    /// native work". <paramref name="backend"/> is <c>LibVlcBackend</c> in production, a fake in
    /// tests — this class never knows which.</summary>
    public VideoEngine(IPlayerBackend backend, IUiThread uiThread, VideoOptions? options = null, string? nativeDirectory = null)
    {
        _backend = backend;
        _ui = uiThread;
        _options = options ?? new VideoOptions();
        _nativeDirectory = nativeDirectory;
        _log = new VideoLog(_options.LogRingSize);
        _loadWatch = new LoadWatch(_options);
    }

    public VideoEngineStatus EngineStatus { get; private set; } = VideoEngineStatus.Asleep;
    public string? EngineFailure { get; private set; }

    public int LiveSurfaces
    {
        get { lock (_gate) return _live.Count; }
    }

    public event Action<VideoEngineStatus>? EngineStatusChanged;

    public Task<VideoEngineStatus> WarmUpAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.FromResult(VideoEngineStatus.Failed);
            if (_warmUpTask is not null)
                return _warmUpTask;

            EngineStatus = VideoEngineStatus.Starting;
            _ui.Post(() => EngineStatusChanged?.Invoke(VideoEngineStatus.Starting));

            _queue = new BlockingCollection<Action>();
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RankMaster2.VideoEngine.Worker" };
            _worker.Start();

            var tcs = new TaskCompletionSource<VideoEngineStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                EngineInitResult result;
                try
                {
                    result = _backend.Initialize(_nativeDirectory, _options, _log.Add);
                }
                catch (Exception ex)
                {
                    result = EngineInitResult.Failed(ex.Message);
                }

                if (result.Success)
                {
                    SetStatus(VideoEngineStatus.Ready);
                    tcs.TrySetResult(VideoEngineStatus.Ready);
                }
                else
                {
                    EngineFailure = $"The video engine could not start: {result.FailureMessage}";
                    SetStatus(VideoEngineStatus.Failed);
                    tcs.TrySetResult(VideoEngineStatus.Failed);
                }
            });

            _warmUpTask = tcs.Task;
            return _warmUpTask;
        }
    }

    public IVideoSurface Create(string paneName)
    {
        VideoSurface surface;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VideoEngine));
            if (_live.Count >= _options.MaxLiveSurfaces)
                throw new InvalidOperationException(
                    $"A video surface beyond the limit of {_options.MaxLiveSurfaces} was requested for pane '{paneName}'. " +
                    "There is never a third player (D-video.md § 1).");

            surface = new VideoSurface(paneName, this, _backend, _ui, _options, _log);
            surface.Released += OnSurfaceReleased;
            _live.Add(surface);
        }

        _ = WarmUpAsync(); // implicit warm-up; never awaited here — Play() awaits it itself
        EnsureLoadWatchTimer();
        return surface;
    }

    private void OnSurfaceReleased(VideoSurface surface)
    {
        lock (_gate)
        {
            surface.Released -= OnSurfaceReleased;
            _live.Remove(surface);
            if (_live.Count == 0)
                _loadWatch.Reset();
        }
    }

    /// <summary>Posts <paramref name="work"/> onto the single worker queue. Every LibVLC call in
    /// <c>Video/</c> reaches the backend only through this (D-video.md § 4.6).</summary>
    internal void Enqueue(Action work)
    {
        BlockingCollection<Action>? q;
        lock (_gate)
            q = _queue;

        if (q is null || q.IsAddingCompleted)
            return; // engine never warmed up, or is shutting down — nothing to run against

        try
        {
            q.Add(work);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with this call — the engine is disposing.
        }
    }

    private void WorkerLoop()
    {
        var queue = _queue!;
        foreach (var action in queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // A worker item must never take the thread down; failures reach surfaces through
                // their own generation-guarded callbacks, not through this loop.
            }
        }
    }

    private void EnsureLoadWatchTimer()
    {
        lock (_gate)
        {
            if (_disposed || _loadWatchTimer is not null)
                return;
            _loadWatchTimer = new Timer(_ => LoadWatchTick(), null, _options.LoadWatchInterval, _options.LoadWatchInterval);
        }
    }

    /// <summary>D-video.md § 4.3's fallback ladder, run once per LoadWatchInterval. Only ever touches
    /// the backend via <see cref="Enqueue"/>, so it obeys the same single-worker-thread rule as
    /// everything else that reaches VLC.</summary>
    private void LoadWatchTick()
    {
        VideoSurface[] live;
        lock (_gate)
            live = _live.ToArray();

        if (live.Length != _options.MaxLiveSurfaces)
            return; // the ladder only applies with a full pair; § 4.3, § 6.1 test 7 "never with one surface"

        var left = live[0];
        var right = live[1];

        Enqueue(() =>
        {
            var leftGen = left.CurrentGeneration;
            var rightGen = right.CurrentGeneration;
            var leftStats = left.SampleStatisticsOnWorker(leftGen);
            var rightStats = right.SampleStatisticsOnWorker(rightGen);
            if (leftStats is null || rightStats is null)
                return; // not both Playing/Holding yet

            var clip = left.ClipLength > right.ClipLength ? left.ClipLength : right.ClipLength;
            var mode = _loadWatch.Sample(left.Stats.LostFrameRate, right.Stats.LostFrameRate, clip,
                DateTimeOffset.UtcNow, _options.ForceAlternate);

            switch (mode)
            {
                case LoadWatchMode.Both:
                    left.SetHolding(false, leftGen);
                    right.SetHolding(false, rightGen);
                    break;
                case LoadWatchMode.AlternateLeftActive:
                    left.SetHolding(false, leftGen);
                    right.SetHolding(true, rightGen);
                    break;
                case LoadWatchMode.AlternateRightActive:
                    left.SetHolding(true, leftGen);
                    right.SetHolding(false, rightGen);
                    break;
            }
        });
    }

    private void SetStatus(VideoEngineStatus status)
    {
        EngineStatus = status;
        _ui.Post(() => EngineStatusChanged?.Invoke(status));
    }

    public string DiagnosticsDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"EngineStatus: {EngineStatus}");
        if (EngineFailure is not null)
            sb.AppendLine($"EngineFailure: {EngineFailure}");
        sb.AppendLine($"LiveSurfaces: {LiveSurfaces}");
        sb.AppendLine($"LoadWatch: {_loadWatch.Mode}");
        sb.AppendLine("-- last log lines --");
        sb.Append(_log.Dump());
        return sb.ToString();
    }

    public void Dispose()
    {
        VideoSurface[] toDispose;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = _live.ToArray();
            _loadWatchTimer?.Dispose();
            _loadWatchTimer = null;
        }

        foreach (var s in toDispose)
            s.Dispose();

        lock (_gate)
            _queue?.CompleteAdding();

        _worker?.Join(TimeSpan.FromSeconds(5));

        try { _backend.Dispose(); }
        catch { /* shutting down */ }
    }
}
