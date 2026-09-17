namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Threading;
using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;

/// <summary>
/// The harness's player (A-startup-and-shell.md § 5.3, § 6.4): a sixty-line loop whose only job is
/// module-usage evidence and frame counts, not full state-machine correctness (that is D's
/// <c>rm2vidprobe</c>, § 6.4's other tool). Buffers real pixels into <see cref="FrameStore"/> so
/// VLC's vout thread always has a valid pointer to write into.
/// </summary>
internal sealed class ProbeCallbacks : IPlayerCallbacks
{
    private readonly FrameStore _frameStore = new();
    private readonly int _paneWidth;
    private readonly int _paneHeight;
    private long _displayCount;
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly ManualResetEventSlim _ended = new(false);
    private readonly ManualResetEventSlim _errored = new(false);
    private int _generation;

    public ProbeCallbacks(int paneWidth = 1920, int paneHeight = 1080)
    {
        _paneWidth = paneWidth;
        _paneHeight = paneHeight;
    }

    public long DisplayCount => Interlocked.Read(ref _displayCount);
    public bool FirstFrameArrived => _firstFrame.IsSet;
    public bool Errored => _errored.IsSet;
    public bool Ended => _ended.IsSet;

    public void Reset(int generation)
    {
        _generation = generation;
        _firstFrame.Reset();
        _ended.Reset();
        _errored.Reset();
        Interlocked.Exchange(ref _displayCount, 0);
    }

    public bool WaitFirstFrame(TimeSpan timeout) =>
        WaitHandle.WaitAny(new[] { _firstFrame.WaitHandle, _ended.WaitHandle, _errored.WaitHandle }, timeout) == 0;

    public (int Width, int Height, int Pitch, int Lines) OnFormat(int sourceWidth, int sourceHeight, int generation)
    {
        var (w, h) = PaneFit.Fit(sourceWidth, sourceHeight, _paneWidth, _paneHeight);
        var stride = PaneFit.Align32(w * 4);
        var lines = PaneFit.Align32(h);
        _frameStore.Allocate(w, h, stride, lines);
        return (w, h, stride, lines);
    }

    public void OnCleanup(int generation) { }

    public IntPtr OnLock(int generation) => _frameStore.NativePointer;

    public void OnDisplay(int generation)
    {
        if (generation != _generation)
            return;
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
            _ended.Set();
    }
}
