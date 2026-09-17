namespace RankMaster2.Pc.Stills;

/// <summary>
/// The wanted set, the frames and failures cached against it, and the (length, mtime) stat key
/// used to detect stale bytes (plan section 3.3 and section 1, "Stale bytes"). Not thread-safe on
/// its own -- <see cref="StillSource"/> holds the lock that makes every call here atomic with the
/// wanted-set decision that provoked it.
/// <para/>
/// <b>Second audit, § 3.13</b> (the desktop twin of § 1.2, proven the same way against
/// <c>MediaFingerprint</c> on the server): every entry here used to be keyed by <c>id</c> alone.
/// Two folders that each held a file called e.g. "holiday.jpg" of the identical byte length and
/// modification time -- StillSource's own staleness key -- collided on one cache entry, so
/// switching from one folder to another without an intervening <see cref="RemoveAll"/> could hand
/// back the first folder's decoded pixels under the second folder's name. Every key here is now
/// <see cref="FrameKey"/>, folder and id together, so two different physical folders can never
/// share an entry no matter what their files are named, sized or stamped. Folding the folder into
/// the wanted-set key also means an ordinary <see cref="SetWanted"/> call after a folder switch
/// evicts the previous folder's entries on its own -- they are simply no longer in the next
/// wanted set once the key includes the folder that changed.
/// </summary>
internal sealed class FrameCache
{
    /// <summary>
    /// A cache entry's identity: the folder a file lives in together with its on-disk filename.
    /// Plain string equality on both -- callers always pass the same on-disk spelling for a given
    /// physical folder within one process (StillSource's own <c>_folder</c> field), so this never
    /// needs the server-side normalisation that reconciles different callers' spellings of the
    /// same directory; it only needs two different directories to never compare equal, which plain
    /// equality already guarantees.
    /// </summary>
    public readonly record struct FrameKey(string Folder, string Id);

    public readonly record struct StatKey(long Length, DateTime LastWriteUtc);
    public readonly record struct FailureEntry(StillFailure Failure, string Detail);

    private readonly HashSet<FrameKey> _wanted = [];
    private readonly Dictionary<FrameKey, StillFrame> _frames = [];
    private readonly Dictionary<FrameKey, FailureEntry> _failures = [];
    private readonly Dictionary<FrameKey, StatKey> _statKeys = [];

    public bool IsWanted(string folder, string id) => _wanted.Contains(new FrameKey(folder, id));

    public StillFrame? TryGetFrame(string folder, string id) => _frames.GetValueOrDefault(new FrameKey(folder, id));

    public FailureEntry? TryGetFailure(string folder, string id) =>
        _failures.TryGetValue(new FrameKey(folder, id), out var entry) ? entry : null;

    public StatKey? TryGetStatKey(string folder, string id) =>
        _statKeys.TryGetValue(new FrameKey(folder, id), out var key) ? key : null;

    public void SetStatKey(string folder, string id, StatKey key) => _statKeys[new FrameKey(folder, id)] = key;

    /// <summary>Stores a freshly decoded frame as the cache's own reference, replacing whatever was there.</summary>
    public void SetFrame(string folder, string id, StillFrame frame)
    {
        var key = new FrameKey(folder, id);
        ClearResultKey(key);
        _frames[key] = frame;
    }

    public void SetFailure(string folder, string id, StillFailure failure, string detail)
    {
        var key = new FrameKey(folder, id);
        ClearResultKey(key);
        _failures[key] = new FailureEntry(failure, detail);
    }

    /// <summary>Releases the cache's own reference to (folder, id)'s frame (if any) and drops any cached failure, but keeps it in the wanted set and keeps its stat key.</summary>
    public void ClearResult(string folder, string id) => ClearResultKey(new FrameKey(folder, id));

    private void ClearResultKey(FrameKey key)
    {
        if (_frames.Remove(key, out var frame))
            frame.Release();
        _failures.Remove(key);
    }

    /// <summary>
    /// Rebuilds the wanted set for <paramref name="folder"/> -- every id in <paramref name="newWanted"/>
    /// is understood to name a file in that folder. Every entry that drops out (because its id is no
    /// longer wanted, OR because it belonged to a folder other than this one -- the normal shape of a
    /// folder switch) has its cached result released immediately, synchronously (plan section 3.3,
    /// "Eviction") -- the old MediaPipeline.EvictUnwanted. Returns the ids that were dropped, so the
    /// caller can also cancel their queued decodes.
    /// </summary>
    public IReadOnlyCollection<string> SetWanted(string folder, IEnumerable<string> newWanted)
    {
        var next = new HashSet<FrameKey>(newWanted.Select(id => new FrameKey(folder, id)));
        var removed = new List<FrameKey>();

        foreach (var key in _wanted)
        {
            if (next.Contains(key)) continue;
            removed.Add(key);
        }

        foreach (var key in removed)
        {
            ClearResultKey(key);
            _statKeys.Remove(key);
        }

        _wanted.Clear();
        _wanted.UnionWith(next);
        return removed.Select(k => k.Id).ToList();
    }

    /// <summary>Plan section 3.8 step 1: forgets everything about (folder, id) and removes it from the wanted set.</summary>
    public void RemoveOne(string folder, string id)
    {
        var key = new FrameKey(folder, id);
        ClearResultKey(key);
        _statKeys.Remove(key);
        _wanted.Remove(key);
    }

    /// <summary>Plan section 3.8/ReleaseAllAsync: drop every frame, forget everything, in every folder.</summary>
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
