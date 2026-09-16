namespace RankMaster2.Pc.Stills;

/// <summary>
/// The wanted set, the frames and failures cached against it, and the (length, mtime) stat key
/// used to detect stale bytes (plan section 3.3 and section 1, "Stale bytes"). Not thread-safe on
/// its own -- <see cref="StillSource"/> holds the lock that makes every call here atomic with the
/// wanted-set decision that provoked it.
/// </summary>
internal sealed class FrameCache
{
    public readonly record struct StatKey(long Length, DateTime LastWriteUtc);
    public readonly record struct FailureEntry(StillFailure Failure, string Detail);

    private readonly HashSet<string> _wanted = [];
    private readonly Dictionary<string, StillFrame> _frames = [];
    private readonly Dictionary<string, FailureEntry> _failures = [];
    private readonly Dictionary<string, StatKey> _statKeys = [];

    public bool IsWanted(string id) => _wanted.Contains(id);

    public StillFrame? TryGetFrame(string id) => _frames.GetValueOrDefault(id);

    public FailureEntry? TryGetFailure(string id) =>
        _failures.TryGetValue(id, out var entry) ? entry : null;

    public StatKey? TryGetStatKey(string id) =>
        _statKeys.TryGetValue(id, out var key) ? key : null;

    public void SetStatKey(string id, StatKey key) => _statKeys[id] = key;

    /// <summary>Stores a freshly decoded frame as the cache's own reference, replacing whatever was there.</summary>
    public void SetFrame(string id, StillFrame frame)
    {
        ClearResult(id);
        _frames[id] = frame;
    }

    public void SetFailure(string id, StillFailure failure, string detail)
    {
        ClearResult(id);
        _failures[id] = new FailureEntry(failure, detail);
    }

    /// <summary>Releases the cache's own reference to id's frame (if any) and drops any cached failure, but keeps id in the wanted set and keeps its stat key.</summary>
    public void ClearResult(string id)
    {
        if (_frames.Remove(id, out var frame))
            frame.Release();
        _failures.Remove(id);
    }

    /// <summary>
    /// Rebuilds the wanted set. Every id that drops out has its cached result released
    /// immediately, synchronously (plan section 3.3, "Eviction") -- the old MediaPipeline.EvictUnwanted.
    /// Returns the ids that were dropped, so the caller can also cancel their queued decodes.
    /// </summary>
    public IReadOnlyCollection<string> SetWanted(IEnumerable<string> newWanted)
    {
        var next = new HashSet<string>(newWanted);
        var removed = new List<string>();

        foreach (var id in _wanted)
        {
            if (next.Contains(id)) continue;
            removed.Add(id);
        }

        foreach (var id in removed)
        {
            ClearResult(id);
            _statKeys.Remove(id);
        }

        _wanted.Clear();
        _wanted.UnionWith(next);
        return removed;
    }

    /// <summary>Plan section 3.8 step 1: forgets everything about one id and removes it from the wanted set.</summary>
    public void RemoveOne(string id)
    {
        ClearResult(id);
        _statKeys.Remove(id);
        _wanted.Remove(id);
    }

    /// <summary>Plan section 3.8/ReleaseAllAsync: drop every frame, forget everything.</summary>
    public void RemoveAll()
    {
        foreach (var frame in _frames.Values)
            frame.Release();
        _frames.Clear();
        _failures.Clear();
        _statKeys.Clear();
        _wanted.Clear();
    }
}
