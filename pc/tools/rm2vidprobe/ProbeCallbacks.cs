namespace RankMaster2.Pc.VidProbe;

using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;

/// <summary>
/// D-video.md § 6.4: the "FrameStore-only sink" — the same <see cref="FrameStore"/>/<see cref="PaneFit"/>
/// path <c>VideoSurface</c> uses, with no Avalonia <c>WriteableBitmap</c>. One instance probes one file
/// at a time; call <see cref="Reset"/> before each. All members may be called from LibVLC's own
/// threads (the four callbacks, the two events) as well as from this tool's single calling thread, so
/// state is kept in thread-safe primitives rather than plain fields where it is read across threads.
/// </summary>
internal sealed class ProbeCallbacks : IPlayerCallbacks
{
    private readonly FrameStore _frameStore = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly ManualResetEventSlim _errored = new(false);
    private readonly ManualResetEventSlim _endReached = new(false);
    private readonly int _paneWidth;
    private readonly int _paneHeight;
    private int _generation;
    private long _displayCount;

    public ProbeCallbacks(int paneWidth = 1920, int paneHeight = 1080)
    {
        _paneWidth = paneWidth;
        _paneHeight = paneHeight;
    }

    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public long DisplayCount => Interlocked.Read(ref _displayCount);
    public bool FirstFrameArrived => _firstFrame.IsSet;
    public bool Errored => _errored.IsSet;
    public bool EndedBeforeFirstFrame => _endReached.IsSet && !_firstFrame.IsSet;

    public void Reset(int generation)
    {
        _generation = generation;
        _firstFrame.Reset();
        _errored.Reset();
        _endReached.Reset();
        SourceWidth = 0;
        SourceHeight = 0;
        Interlocked.Exchange(ref _displayCount, 0);
    }

    /// <summary>Blocks the calling thread until a first frame arrives, an error is raised, or the
    /// clip ends before either — whichever comes first, or the timeout.</summary>
    public bool WaitFirstOutcome(TimeSpan timeout) =>
        WaitHandle.WaitAny(
            new[] { _firstFrame.WaitHandle, _errored.WaitHandle, _endReached.WaitHandle },
            timeout) != WaitHandle.WaitTimeout;

    public (int Width, int Height, int Pitch, int Lines) OnFormat(int sourceWidth, int sourceHeight, int generation)
    {
        if (generation != _generation)
        {
            var (fw, fh) = PaneFit.Fit(sourceWidth, sourceHeight, _paneWidth, _paneHeight);
            return (fw, fh, PaneFit.Align32(fw * 4), PaneFit.Align32(fh));
        }

        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        var (w, h) = PaneFit.Fit(sourceWidth, sourceHeight, _paneWidth, _paneHeight);
        var pitch = PaneFit.Align32(w * 4);
        var lines = PaneFit.Align32(h);
        _frameStore.Allocate(w, h, pitch, lines);
        return (w, h, pitch, lines);
    }

    public void OnCleanup(int generation)
    {
        if (generation == _generation)
            _frameStore.Free();
    }

    public IntPtr OnLock(int generation) => _frameStore.NativePointer;

    public void OnDisplay(int generation)
    {
        if (generation != _generation)
            return;
        _frameStore.OnDisplay();
        Interlocked.Increment(ref _displayCount);
        _firstFrame.Set();
    }

    public void OnEncounteredError(int generation)
    {
        if (generation == _generation)
            _errored.Set();
    }

    public void OnEndReached(int generation)
    {
        if (generation == _generation)
            _endReached.Set();
    }
}
