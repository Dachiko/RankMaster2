namespace RankMaster2.Pc.Stills;

// The pc/plans/C-stills.md section 2.1 seam, C -> E. No integrator-frozen file existed in this
// worktree when this part was built (see the note in RankMaster2.Pc.Stills.csproj), so this is
// the shape the plan argues for, taken verbatim from the plan text.

/// <summary>
/// Pixels for the ranking surface. One instance per process. Every method is safe to call from
/// any thread; <see cref="Changed"/> is raised on a worker thread (or synchronously inside
/// <see cref="Show"/>), never on a UI thread -- E marshals.
/// </summary>
public interface IStillSource : IAsyncDisposable
{
    /// <summary>
    /// The size of one pane in PHYSICAL pixels (E multiplies by its render scaling). Stored; starts
    /// nothing. The next Show/Warm decodes to it. Before the first call the size is 960 x 1080.
    /// Values below 16 are ignored.
    /// </summary>
    void SetPaneSize(int widthPx, int heightPx);

    /// <summary>
    /// Make these two ids the visible pair. <paramref name="folder"/> is snapshot.folder verbatim;
    /// ids are pair.left.id / pair.right.id verbatim (on-disk spelling). Rebuilds the wanted set
    /// as {left, right} union warm, evicts everything outside it, queues decodes for whichever of
    /// the two has no usable frame, and raises Changed for BOTH ids synchronously before returning
    /// (Ready if a frame is already cached, otherwise Pending).
    /// </summary>
    void Show(string folder, string leftId, string rightId);

    /// <summary>
    /// The server's warmPairs, oldest first, as (leftId, rightId). Replaces the previous warm list
    /// entirely; at most <paramref name="prefetchPairs"/> pairs are kept (extra ones ignored).
    /// Never raises Changed. Never counts as anything. Call after Show, with the same folder.
    /// </summary>
    void Warm(string folder, IReadOnlyList<(string LeftId, string RightId)> pairs, int prefetchPairs = 2);

    /// <summary>Current state of an id. Unknown ids are Pending.</summary>
    StillState StateOf(string id);

    /// <summary>State transitions for ids in the wanted set. Not raised for warm-only ids.</summary>
    event Action<string, StillState>? Changed;

    /// <summary>
    /// Forget everything about <paramref name="id"/> and return only when this part holds no file
    /// handle on it: the frame is dropped from the cache, any queued decode is removed, and an
    /// in-flight decode is waited for (a Skia decode cannot be interrupted; it is short). Then
    /// the file is probed for an exclusive open; returns false if something else still holds it
    /// after 2 s. E calls this before discard / special and before undo of a move. A file the
    /// server restores under a new name (lastAction.restoredId) is simply a new id: nothing to do.
    /// </summary>
    Task<bool> ReleaseAsync(string folder, string id, CancellationToken ct);

    /// <summary>Drop every frame, empty every queue, wait for in-flight decodes. Called on folder close.</summary>
    Task ReleaseAllAsync(CancellationToken ct);
}

public abstract record StillState
{
    /// <summary>Queued or decoding. E shows the filename and a spinner.</summary>
    public sealed record Pending : StillState;

    /// <summary>
    /// Pixels exist. The lease is E's, and E MUST dispose it -- when it is finished with the frame,
    /// which for a pane is when that pane stops showing it, not when the first bitmap copy is made
    /// (a pane kept for the next pair goes on being repainted from the same lease). A delivery E
    /// does not take up at all is disposed on the spot. The buffer stays valid until the last lease
    /// is gone even if the cache has evicted the frame meanwhile -- which is the whole point: it is
    /// what lets a pane keep painting while C re-decodes the same id at a new pane size.
    /// </summary>
    public sealed record Ready(StillLease Lease) : StillState;

    /// <summary>The file could not be shown. Detail is one line for a toast; never a stack trace.</summary>
    public sealed record Failed(StillFailure Reason, string Detail) : StillState;
}

public enum StillFailure
{
    /// <summary>File or folder not found. E: "file is gone"; only that side's discard is offered (-> drop_missing).</summary>
    Missing,

    /// <summary>Exists but cannot be read: sharing violation, permissions, I/O error. E: "cannot be read"; offer retry (re-Show) and discard.</summary>
    Unreadable,

    /// <summary>Exists, readable, not a decodable image. E: "not a valid image"; offer discard (ordinary, with undo) or a vote for the other side.</summary>
    NotAnImage,

    /// <summary>A real image whose decode would exceed the pixel budget on its own. E: same offers as NotAnImage, different sentence.</summary>
    TooLarge,
}

/// <summary>A reference to a frame. Dispose exactly once. Thread-agnostic.</summary>
public sealed class StillLease : IDisposable
{
    private readonly StillFrame _frame;
    private int _disposed;

#if DEBUG
    private readonly string _debugId;
    private bool _finalizerArmed = true;

    ~StillLease()
    {
        if (_finalizerArmed && Volatile.Read(ref _disposed) == 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"StillLease for '{_debugId}' was never disposed -- the frame's buffer leaks until process exit.");
        }
    }
#endif

    internal StillLease(StillFrame frame)
    {
        _frame = frame;
        frame.AddRef();
#if DEBUG
        _debugId = frame.Id;
#endif
    }

    public StillFrame Frame => _frame;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return; // a lease disposed twice is a no-op

        _frame.Release();
#if DEBUG
        _finalizerArmed = false;
        GC.SuppressFinalize(this);
#endif
    }
}
