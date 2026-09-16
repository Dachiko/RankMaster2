namespace RankMaster2.Pc.Stills;

/// <summary>
/// <see cref="IStillSource"/> over <see cref="FrameCache"/> and <see cref="DecodeQueue"/> -- the
/// only public entry point besides <see cref="MediaProbe"/>. One <see cref="_gate"/> lock makes
/// every wanted-set decision atomic with the cache/queue state it reads and writes; decodes run on
/// the queue's own worker threads and report back through <see cref="OnDecoded"/>.
/// </summary>
public sealed class StillSource : IStillSource
{
    private readonly object _gate = new();
    private readonly FrameCache _cache = new();
    private readonly IStillDecoder _decoder;
    private readonly DecodeQueue _queue;

    private string? _folder;
    private (string Left, string Right)? _visible;
    private HashSet<string> _warmIds = [];
    private int _paneW = 960;
    private int _paneH = 1080;
    private bool _disposed;

    public event Action<string, StillState>? Changed;

    public StillSource(DecodeBudget budget) : this(new StillDecoder(budget))
    {
    }

    internal StillSource(IStillDecoder decoder)
    {
        _decoder = decoder;
        _queue = new DecodeQueue(decoder);
    }

    /// <summary>Test-only: lets StillSourceTests observe worker/queue state without a second seam.</summary>
    internal DecodeQueue DebugQueue => _queue;

    public void SetPaneSize(int widthPx, int heightPx)
    {
        if (widthPx < 16 || heightPx < 16)
            return;

        lock (_gate)
        {
            _paneW = widthPx;
            _paneH = heightPx;
        }
    }

    public void Show(string folder, string leftId, string rightId)
    {
        lock (_gate)
        {
            _folder = folder;
            _visible = (leftId, rightId);
            RebuildWantedLocked();
            EnsureFreshLocked(folder, leftId, DecodeQueue.VisibleRank);
            EnsureFreshLocked(folder, rightId, DecodeQueue.VisibleRank);
        }

        RaiseChanged(leftId);
        RaiseChanged(rightId);
    }

    public void Warm(string folder, IReadOnlyList<(string LeftId, string RightId)> pairs, int prefetchPairs = 2)
    {
        lock (_gate)
        {
            _folder = folder;
            var kept = pairs.Take(Math.Max(0, prefetchPairs));

            // A HashSet for wanted-set membership, but an ordered list for enqueueing: plan
            // section 3.5's "warm ids decode one at a time" is easiest to reason about (and to
            // test) when that one-at-a-time order is the order the ids arrived in.
            var warmSet = new HashSet<string>();
            var warmOrder = new List<string>();
            foreach (var (left, right) in kept)
            {
                if (warmSet.Add(left)) warmOrder.Add(left);
                if (warmSet.Add(right)) warmOrder.Add(right);
            }
            _warmIds = warmSet;

            RebuildWantedLocked();
            foreach (var id in warmOrder)
                EnsureFreshLocked(folder, id, DecodeQueue.WarmRank);
        }
        // Warm never raises Changed (plan section 2.1).
    }

    public StillState StateOf(string id)
    {
        lock (_gate)
        {
            return StateOfLocked(id);
        }
    }

    public async Task<bool> ReleaseAsync(string folder, string id, CancellationToken ct)
    {
        Task? inFlight;
        lock (_gate)
        {
            _queue.CancelQueued(id);
            inFlight = _queue.InFlight(id);
            _cache.RemoveOne(id);
            _warmIds.Remove(id);
            // _visible is deliberately left alone: real callers always issue a fresh Show() with
            // the next pair right after a release (E's contract per PC_CLIENT_PLAN.md section
            // 6.5), which overwrites it before any wanted-set rebuild happens. Clearing it here
            // instead would stop Changed firing for id's still-in-flight sibling in the meantime.
        }

        if (inFlight is not null)
            await inFlight.WaitAsync(ct).ConfigureAwait(false);

        var path = Path.Combine(folder, id);
        return await HandleCheck.CanOpenExclusivelyAsync(path, ct).ConfigureAwait(false);
    }

    public async Task ReleaseAllAsync(CancellationToken ct)
    {
        IReadOnlyList<Task> inFlight;
        lock (_gate)
        {
            inFlight = _queue.DrainAndCollectInFlight();
        }

        foreach (var task in inFlight)
            await task.WaitAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            _cache.RemoveAll();
            _folder = null;
            _visible = null;
            _warmIds = [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ReleaseAllAsync(CancellationToken.None).ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private void RebuildWantedLocked()
    {
        var wanted = new HashSet<string>(_warmIds);
        if (_visible is { } v)
        {
            wanted.Add(v.Left);
            wanted.Add(v.Right);
        }

        var removed = _cache.SetWanted(wanted);
        foreach (var id in removed)
            _queue.CancelQueued(id);
    }

    /// <summary>
    /// Plan section 1 ("Stale bytes") and section 3.1 ("Reusing a frame after the pane changed
    /// size"): decides whether id already has a usable, fresh result, or needs a decode queued.
    /// Called with <see cref="_gate"/> held.
    /// </summary>
    private void EnsureFreshLocked(string folder, string id, int rank)
    {
        var path = Path.Combine(folder, id);
        var key = Stat(path);

        if (key is null)
        {
            _cache.SetFailure(id, StillFailure.Missing, $"{id}: file is gone.");
            _queue.CancelQueued(id);
            return;
        }

        var cachedKey = _cache.TryGetStatKey(id);
        if (cachedKey is not null && cachedKey.Value.Equals(key.Value))
        {
            var frame = _cache.TryGetFrame(id);
            if (frame is not null)
            {
                if (DecodeGeometry.FrameCovers(frame.Width, frame.Height, frame.SourceWidth, frame.SourceHeight, _paneW, _paneH))
                    return; // fresh, and big enough for the current pane
            }
            else if (_cache.TryGetFailure(id) is not null)
            {
                return; // a fresh, already-known failure -- no need to redecode
            }
        }

        _cache.ClearResult(id);
        _cache.SetStatKey(id, key.Value);
        _queue.Enqueue(folder, id, path, _paneW, _paneH, rank, OnDecoded);
    }

    private void OnDecoded(string folder, string id, DecodeResult result)
    {
        bool wanted;
        bool isVisible;
        lock (_gate)
        {
            wanted = folder == _folder && _cache.IsWanted(id);
            isVisible = _visible is { } v && (v.Left == id || v.Right == id);
            if (wanted)
            {
                if (result.IsSuccess)
                    _cache.SetFrame(id, result.Frame!);
                else
                    _cache.SetFailure(id, result.Failure, result.Detail!);
            }
        }

        if (!wanted)
        {
            result.Frame?.Release();
            return;
        }

        if (isVisible)
            RaiseChanged(id);
    }

    private void RaiseChanged(string id)
    {
        // StateOfLocked acquires a fresh lease for a Ready state; that lease is E's to dispose
        // (plan section 2.1). Building it with nobody to hand it to would leak it forever, since
        // nothing else ever disposes a lease nobody received -- so this checks for a subscriber
        // BEFORE constructing the state, not after.
        var handler = Changed;
        if (handler is null)
            return;

        StillState state;
        lock (_gate)
        {
            state = StateOfLocked(id);
        }
        handler.Invoke(id, state);
    }

    private StillState StateOfLocked(string id)
    {
        var frame = _cache.TryGetFrame(id);
        if (frame is not null)
            return new StillState.Ready(frame.Lease());

        var failure = _cache.TryGetFailure(id);
        if (failure is { } f)
            return new StillState.Failed(f.Failure, f.Detail);

        return new StillState.Pending();
    }

    private static FrameCache.StatKey? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FrameCache.StatKey(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
