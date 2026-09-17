namespace RankMaster2.Pc.Video;

using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RankMaster2.Pc.Video.Backend;

/// <summary>
/// The D → E seam's implementation: the state machine of D-video.md § 4.5, the generation counter of
/// § 4.4 rule 2, and the frame path of § 4.7. Avalonia types (<see cref="WriteableBitmap"/>) live only
/// here and in <see cref="IVideoSurface"/> itself — everything native lives behind
/// <see cref="IPlayerBackend"/>, reached only through <see cref="VideoEngine"/>'s worker queue.
/// </summary>
public sealed class VideoSurface : IVideoSurface, IPlayerCallbacks
{
    private static readonly VideoStats EmptyStats = new(0, 0, "", 0, 0, 0.0, false, TimeSpan.Zero);

    // A22/§ 6.5: the "D" mark the startup kit needs to answer the owner's own question about what
    // annoys him at launch. Once per process — the first frame of the first surface any pane ever
    // shows, not once per surface.
    private static int _firstVideoFrameMarked;

    private readonly VideoEngine _engine;
    private readonly IPlayerBackend _backend;
    private readonly IUiThread _ui;
    private readonly VideoOptions _options;
    private readonly VideoLog _log;
    private readonly FrameStore _frameStore = new();
    private readonly object _gate = new();

    private IBackendPlayer? _player;
    private int _generation;
    private int _paneWidth = 1920;
    private int _paneHeight = 1080;
    private WriteableBitmap? _bitmap;
    private DateTimeOffset _playStarted;

    /// <summary>Raised once, from <see cref="Dispose"/>, so <see cref="VideoEngine"/> can drop this
    /// surface from its live set and decrement <see cref="IVideoSurfaceFactory.LiveSurfaces"/>.</summary>
    internal event Action<VideoSurface>? Released;

    internal VideoSurface(string paneName, VideoEngine engine, IPlayerBackend backend, IUiThread ui, VideoOptions options, VideoLog log)
    {
        PaneName = paneName;
        _engine = engine;
        _backend = backend;
        _ui = ui;
        _options = options;
        _log = log;
        Stats = EmptyStats;
    }

    public string PaneName { get; }
    public VideoSurfaceState State { get; private set; } = VideoSurfaceState.Idle;
    public string? Path { get; private set; }
    public Bitmap? Frame { get; private set; }
    public PixelSize FrameSize { get; private set; }
    public VideoFailure? Failure { get; private set; }
    public VideoStats Stats { get; private set; }

    public event Action<IVideoSurface>? StateChanged;
    public event Action<IVideoSurface>? FrameChanged;

    // Exposed for VideoEngine's alternate-mode coordinator (§ 4.3), always invoked from inside an
    // action already running on the engine's worker thread.
    internal int CurrentGeneration => Volatile.Read(ref _generation);
    internal TimeSpan ClipLength => _player?.Length ?? TimeSpan.Zero;

    public void SetPaneSize(PixelSize pixels)
    {
        if (Volatile.Read(ref _disposedFlag) != 0)
            throw new ObjectDisposedException(nameof(VideoSurface));
        if (pixels.Width >= 16)
            Volatile.Write(ref _paneWidth, pixels.Width);
        if (pixels.Height >= 16)
            Volatile.Write(ref _paneHeight, pixels.Height);
    }

    // A lock-free disposed flag: SetPaneSize/Play/Dispose need to check it from arbitrary threads
    // without taking _gate.
    private int _disposedFlag;

    public void Play(string path)
    {
        if (Volatile.Read(ref _disposedFlag) != 0)
            throw new ObjectDisposedException(nameof(VideoSurface));

        lock (_gate)
        {
            var samePath = PathsMatch(Path, path);
            if (samePath && State is VideoSurfaceState.Opening or VideoSurfaceState.Playing or VideoSurfaceState.Holding)
                return; // no-op: already (becoming) this path
        }

        _ = PlayCoreAsync(path);
    }

    private async Task PlayCoreAsync(string path)
    {
        await StopAsync().ConfigureAwait(false);

        int gen;
        lock (_gate)
        {
            gen = ++_generation;
            Path = path;
            Failure = null;
            Frame = null;
            FrameSize = default;
            Stats = EmptyStats;
        }

        SetState(VideoSurfaceState.Opening, gen);

        if (_engine.EngineStatus == VideoEngineStatus.Failed)
        {
            FailEngineUnavailable(gen);
            return;
        }

        var warm = await _engine.WarmUpAsync().ConfigureAwait(false);
        if (gen != Volatile.Read(ref _generation))
            return;
        if (warm != VideoEngineStatus.Ready)
        {
            FailEngineUnavailable(gen);
            return;
        }

        _engine.Enqueue(() => WorkerPlay(path, gen));
    }

    private void FailEngineUnavailable(int gen) =>
        Fail(gen, new VideoFailure(VideoFailureKind.EngineUnavailable,
            _engine.EngineFailure ?? "The video engine could not start.", null));

    private void WorkerPlay(string path, int gen)
    {
        if (gen != Volatile.Read(ref _generation))
            return;

        // § 4.5 step 1: File.Exists false — no backend call.
        if (!File.Exists(path))
        {
            Fail(gen, FailureMapper.Map(FailureSignal.FileMissing));
            return;
        }

        lock (_gate)
            _player ??= _backend.CreatePlayer(this);
        var player = _player!;

        // § 4.5 step 2: demux-only parse.
        var parse = player.Parse(path, _options.ParseTimeout);
        if (gen != Volatile.Read(ref _generation))
            return;

        if (parse.Outcome == ParseOutcome.Timeout)
        {
            Fail(gen, FailureMapper.Map(FailureSignal.ParseTimedOut, LastLog()));
            return;
        }

        if (parse.Outcome == ParseOutcome.Failed)
        {
            Fail(gen, FailureMapper.Map(FailureSignal.ParseFailed, LastLog()));
            return;
        }

        // § 4.5 step 3: has a video track?
        if (!parse.HasVideoTrack)
        {
            Fail(gen, FailureMapper.Map(FailureSignal.NoVideoTrack, LastLog()));
            return;
        }

        lock (_gate)
            Stats = Stats with { SourceWidth = parse.Width, SourceHeight = parse.Height, Codec = parse.Codec };

        _playStarted = DateTimeOffset.UtcNow;

        // § 4.5 step 4: start playback.
        if (!player.Play(_options, gen))
        {
            Fail(gen, FailureMapper.Map(FailureSignal.PlayReturnedFalse, LastLog()));
            return;
        }

        ArmFirstFrameTimeout(gen);
    }

    private void ArmFirstFrameTimeout(int gen)
    {
        _ = Task.Delay(_options.FirstFrameTimeout).ContinueWith(_ =>
        {
            if (gen != Volatile.Read(ref _generation))
                return;
            _engine.Enqueue(() =>
            {
                if (gen != Volatile.Read(ref _generation))
                    return;
                if (State != VideoSurfaceState.Opening)
                    return; // § 4.5 step 8 already happened
                Fail(gen, FailureMapper.Map(FailureSignal.NoFrameWithinTimeout, LastLog(), _options.FirstFrameTimeout));
            });
        }, TaskScheduler.Default);
    }

    // ---- IPlayerCallbacks — invoked by the backend from its own (VLC) threads. ----

    (int Width, int Height, int Pitch, int Lines) IPlayerCallbacks.OnFormat(int sourceWidth, int sourceHeight, int generation)
    {
        var (w, h) = PaneFit.Fit(sourceWidth, sourceHeight, Volatile.Read(ref _paneWidth), Volatile.Read(ref _paneHeight));
        var pitch = PaneFit.Align32(w * 4);
        var lines = PaneFit.Align32(h);

        // VLC still needs a valid answer even for a stale/late callback (§ 4.4 rule 2 says the *effect*
        // is ignored, not that VLC goes unanswered) — but a stale call must never touch the shared
        // FrameStore or bitmap, which by now belong to a newer generation.
        if (generation == Volatile.Read(ref _generation))
        {
            _frameStore.Allocate(w, h, pitch, lines);

            _ui.Post(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                    return;
                if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
                {
                    _bitmap?.Dispose();
                    _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                }
            });
        }

        return (w, h, pitch, lines);
    }

    void IPlayerCallbacks.OnCleanup(int generation)
    {
        // Idempotent and, like OnFormat, generation-guarded so a stale cleanup can never free a
        // newer generation's buffer out from under it. A legitimate cleanup for the current
        // generation is redundant with StopAsync's own Free() and safe to run twice.
        if (generation == Volatile.Read(ref _generation))
            _frameStore.Free();
    }

    IntPtr IPlayerCallbacks.OnLock(int generation) => _frameStore.NativePointer;

    void IPlayerCallbacks.OnDisplay(int generation)
    {
        if (_frameStore.OnDisplay())
            _ui.Post(() => Present(generation));
    }

    void IPlayerCallbacks.OnEncounteredError(int generation)
    {
        _engine.Enqueue(() =>
        {
            if (generation != Volatile.Read(ref _generation))
                return;

            if (State is VideoSurfaceState.Playing or VideoSurfaceState.Holding)
            {
                // § 4.5 step 9: Frame is kept so the pane does not go black; E decides.
                Failure = FailureMapper.Map(FailureSignal.EncounteredErrorWhilePlaying, LastLog());
                SetState(VideoSurfaceState.Failed, generation);
            }
            else
            {
                // § 4.5 step 5.
                Fail(generation, FailureMapper.Map(FailureSignal.EncounteredErrorBeforeFirstFrame, LastLog()));
            }
        });
    }

    void IPlayerCallbacks.OnEndReached(int generation)
    {
        _engine.Enqueue(() =>
        {
            if (generation != Volatile.Read(ref _generation))
                return;

            if (State == VideoSurfaceState.Opening)
            {
                // § 4.5 step 6.
                Fail(generation, FailureMapper.Map(FailureSignal.EndReachedBeforeFirstFrame, LastLog()));
                return;
            }

            // § 4.5 step 10: the belt half of looping (:input-repeat is the braces).
            _player?.Restart();
        });
    }

    private void Present(int generation)
    {
        if (generation != Volatile.Read(ref _generation))
            return;

        var front = _frameStore.BeginPresent();
        if (front is null || _bitmap is null)
            return;

        var stride = _frameStore.Stride;
        var height = _bitmap.PixelSize.Height;
        using (var fb = _bitmap.Lock())
        {
            // front is stride * Align32(height) bytes — VLC's OnFormat told it that many lines exist
            // (§ 4.7) — but the bitmap is only stride-ish * height. Copying front's full length here
            // overflows the bitmap's own allocation by the padding rows; only ever copy `height` rows,
            // one memcpy when the strides match, row by row when they don't.
            if (fb.RowBytes == stride)
            {
                Marshal.Copy(front, 0, fb.Address, stride * height);
            }
            else
            {
                var rowBytes = Math.Min(fb.RowBytes, stride);
                for (var y = 0; y < height; y++)
                    Marshal.Copy(front, y * stride, fb.Address + y * fb.RowBytes, rowBytes);
            }
        }

        Frame = _bitmap;
        FrameSize = _bitmap.PixelSize;

        if (State == VideoSurfaceState.Opening)
        {
            // § 4.5 step 8.
            Stats = Stats with { TimeToFirstFrame = DateTimeOffset.UtcNow - _playStarted };
            State = VideoSurfaceState.Playing;
            StateChanged?.Invoke(this);
        }

        if (Interlocked.CompareExchange(ref _firstVideoFrameMarked, 1, 0) == 0)
            RankMaster2.Pc.App.StartupClock.Mark("first_video_frame");

        FrameChanged?.Invoke(this);
    }

    public async Task StopAsync()
    {
        int gen;
        IBackendPlayer? player;
        lock (_gate)
        {
            gen = ++_generation;
            player = _player;
        }

        var path = Path;
        Path = null;

        if (player is not null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _engine.Enqueue(() =>
            {
                try { player.Stop(); }
                finally { tcs.TrySetResult(); }
            });
            await tcs.Task.ConfigureAwait(false);
        }

        _frameStore.Free();
        Frame = null;
        FrameSize = default;

        if (path is not null)
            await WaitReleasedAsync(path).ConfigureAwait(false);

        SetState(VideoSurfaceState.Idle, gen);
    }

    private async Task WaitReleasedAsync(string path)
    {
        for (var i = 0; i < _options.ReleaseWaitAttempts; i++)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(_options.ReleaseWaitInterval).ConfigureAwait(false);
            }
            catch
            {
                return; // file already gone, or a non-lock error — nothing more this part can do
            }
        }

        _log.Add($"[warn] {PaneName}: {path} was not released within " +
                 $"{_options.ReleaseWaitAttempts * _options.ReleaseWaitInterval.TotalMilliseconds:0}ms");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
            return; // idempotent

        StopAsync().GetAwaiter().GetResult();

        lock (_gate)
        {
            _player?.Dispose();
            _player = null;
        }

        _frameStore.Free();
        Frame = null;
        var bitmap = _bitmap;
        _bitmap = null;
        _ui.Post(() => bitmap?.Dispose());

        Released?.Invoke(this);
    }

    /// <summary>Called by <see cref="VideoEngine"/>'s alternate-mode coordinator, always from inside
    /// an action already running on the worker thread. Not part of the public seam.</summary>
    internal void SetHolding(bool holding, int generation)
    {
        if (generation != Volatile.Read(ref _generation))
            return;
        if (State != VideoSurfaceState.Playing && State != VideoSurfaceState.Holding)
            return;

        _player?.SetPause(holding);
        Stats = Stats with { Alternating = true };
        SetState(holding ? VideoSurfaceState.Holding : VideoSurfaceState.Playing, generation);
    }

    /// <summary>Called by <see cref="VideoEngine"/>'s alternate-mode coordinator on the worker
    /// thread: refreshes <see cref="Stats"/> from the backend's counters and returns them so the
    /// coordinator can feed <see cref="LoadWatch"/>.</summary>
    internal BackendStatistics? SampleStatisticsOnWorker(int generation)
    {
        if (generation != Volatile.Read(ref _generation))
            return null;
        if (_player is null || (State != VideoSurfaceState.Playing && State != VideoSurfaceState.Holding))
            return null;

        var stats = _player.Statistics();
        var total = stats.DecodedFrames + stats.LostFrames;
        var rate = total > 0 ? stats.LostFrames / (double)total : 0.0;
        Stats = Stats with { DecodedFrames = stats.DecodedFrames, LostFrames = stats.LostFrames, LostFrameRate = rate };
        return stats;
    }

    private void Fail(int gen, VideoFailure failure)
    {
        if (gen != Volatile.Read(ref _generation))
            return;
        Failure = failure;
        SetState(VideoSurfaceState.Failed, gen);
    }

    private void SetState(VideoSurfaceState state, int gen)
    {
        _ui.Post(() =>
        {
            if (gen != Volatile.Read(ref _generation))
                return;
            State = state;
            StateChanged?.Invoke(this);
        });
    }

    private IReadOnlyList<string> LastLog() => _log.Last(20);

    private static bool PathsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
