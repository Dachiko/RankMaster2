using RankMaster2.Pc.Link;
using WireSnapshot = RankMaster2.Pc.Link.Wire.Snapshot;

namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>A scriptable <see cref="ISessionLink"/> (plan § "E0 — Seam intake": "FakeSessionLink
/// scripted with a list of results"). Every call is recorded so a test can assert exactly what was
/// sent and how many times; every call's result is whatever the test enqueued, or a default.</summary>
public sealed class FakeSessionLink : ISessionLink
{
    public LinkState State { get; set; } = LinkState.InSession;
    public WireSnapshot? Snapshot { get; set; }
    public Failure? LastFailure { get; set; }
    public bool IsBusy { get; set; }

    public sealed record Call(string Method, Side? Side, long? PairSeq)
    {
        /// <summary>The folder a BrowseAsync call asked for (plan I); null for every other call.</summary>
        public string? Path { get; init; }
    }
    public readonly List<Call> Calls = new();

    public Queue<ConnectResult> ConnectResults { get; } = new();
    public Queue<OpenResult> OpenResults { get; } = new();

    /// <summary>Set to make the next <see cref="UndoAsync"/> call throw instead of returning (H9
    /// coverage: <c>TryUndoFromStartAsync</c> must not wedge the start screen when the link's busy
    /// gate throws, the same gap <see cref="OpenAsync"/>'s no-scripted-result throw covers).</summary>
    public Exception? UndoThrows { get; set; }

    /// <summary>Shared by Vote/Skip/Discard/Special/Undo/Save/Refresh -- dequeued in call order.
    /// Defaults to Applied(Snapshot) (or a NotSent(NoSession) if Snapshot is null) when empty.</summary>
    public Queue<ActionResult> ActionResults { get; } = new();

    /// <summary>Set to make every action complete on a thread-pool thread instead of synchronously,
    /// as a real HTTP call does. That is what makes the UI-thread rule of AUDIT2.md § 4.5 observable:
    /// with a synchronous fake, the continuation is always already on the caller's thread and any
    /// missing marshalling is invisible.</summary>
    public bool CompleteOffThread { get; set; }

    private Task<T> Complete<T>(T value) => CompleteOffThread ? Task.Run(() => value) : Task.FromResult(value);

    public Task<ConnectResult> ConnectAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(ConnectAsync), null, null));
        return Task.FromResult(ConnectResults.Count > 0 ? ConnectResults.Dequeue() : new ConnectResult.Connected("https://fake", false, false));
    }

    public Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(OpenAsync), null, null));
        if (ThrowOnOverlappingBrowseCalls && BrowseInFlight > 0)
            throw new InvalidOperationException("FakeSessionLink: a link call is already in flight (the real link's gate).");
        if (OpenResults.Count > 0) return Task.FromResult(OpenResults.Dequeue());
        if (Snapshot is not null) return Task.FromResult<OpenResult>(new OpenResult.Opened(Snapshot, false));
        throw new InvalidOperationException("FakeSessionLink.OpenAsync: no scripted OpenResult and no default Snapshot set.");
    }

    public Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(VoteAsync), winner, onPairSeq));
        return Complete(NextActionResult());
    }

    public Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(SkipAsync), null, onPairSeq));
        return Complete(NextActionResult());
    }

    public Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(DiscardAsync), side, onPairSeq));
        return Complete(NextActionResult());
    }

    public Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(SpecialAsync), side, onPairSeq));
        return Complete(NextActionResult());
    }

    public Task<ActionResult> UndoAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(UndoAsync), null, null));
        if (UndoThrows is { } ex) throw ex;
        return Complete(NextActionResult());
    }

    public Task<ActionResult> SaveAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(SaveAsync), null, null));
        return Complete(NextActionResult());
    }

    public Task<ActionResult> RefreshAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(RefreshAsync), null, null));
        return Complete(NextActionResult());
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(CloseAsync), null, null));
        return Task.CompletedTask;
    }

    // ---- rename by rank (§ 3.13) -------------------------------------------------------------

    public Queue<RenameOperationResult> StartRenameResults { get; } = new();
    public Queue<RenameOperationResult> GetRenameResults { get; } = new();
    public Queue<RenameOperationResult> CancelRenameResults { get; } = new();

    public Task<RenameOperationResult> StartRenameAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(StartRenameAsync), null, null));
        if (StartRenameResults.Count > 0) return Task.FromResult(StartRenameResults.Dequeue());
        throw new InvalidOperationException("FakeSessionLink.StartRenameAsync: no scripted RenameOperationResult.");
    }

    public Task<RenameOperationResult> GetRenameAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(GetRenameAsync), null, null));
        if (GetRenameResults.Count > 0) return Task.FromResult(GetRenameResults.Dequeue());
        throw new InvalidOperationException("FakeSessionLink.GetRenameAsync: no scripted RenameOperationResult.");
    }

    public Task<RenameOperationResult> CancelRenameAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(CancelRenameAsync), null, null));
        if (CancelRenameResults.Count > 0) return Task.FromResult(CancelRenameResults.Dequeue());
        throw new InvalidOperationException("FakeSessionLink.CancelRenameAsync: no scripted RenameOperationResult.");
    }

    // ---- folder browser (plan I) -----------------------------------------------------------------

    /// <summary>What GetRootsAsync answers. Empty by default.</summary>
    public List<RankMaster2.Pc.Link.Wire.LibraryRoot> Roots { get; } = new();

    /// <summary>What BrowseAsync answers, by path (exact string). A path not in here answers
    /// <see cref="ListingResult.Failed"/> with <see cref="FailureKind.FolderNotFound"/>.</summary>
    public Dictionary<string, RankMaster2.Pc.Link.Wire.FolderListing> Listings { get; } = new();

    /// <summary>When set, the next GetRootsAsync / BrowseAsync answers exactly this instead.</summary>
    public Queue<RootsResult> RootsResults { get; } = new();
    public Queue<ListingResult> ListingResults { get; } = new();

    /// <summary>When set, BrowseAsync awaits this before answering, so a test can hold a listing in
    /// flight (e.g. to check a stale answer is dropped).</summary>
    public Func<string, Task>? BeforeBrowseAnswer { get; set; }

    /// <summary>The real link runs every call through one gate: a second call while one is in flight throws
    /// <see cref="InvalidOperationException"/>. Set this to make the fake do the same for overlapping
    /// GetRootsAsync / BrowseAsync / OpenAsync calls, so a test proves the browser never overlaps them.</summary>
    public bool ThrowOnOverlappingBrowseCalls { get; set; }

    /// <summary>How many GetRootsAsync / BrowseAsync calls are in flight now, and the most there ever were.</summary>
    public int BrowseInFlight { get; private set; }
    public int MaxBrowseInFlight { get; private set; }

    private void EnterBrowseCall()
    {
        lock (Calls)
        {
            if (ThrowOnOverlappingBrowseCalls && BrowseInFlight > 0)
                throw new InvalidOperationException("FakeSessionLink: a link call is already in flight (the real link's gate).");
            BrowseInFlight++;
            MaxBrowseInFlight = Math.Max(MaxBrowseInFlight, BrowseInFlight);
        }
    }

    private void LeaveBrowseCall() { lock (Calls) BrowseInFlight--; }

    private static ListingResult.Failed Cancelled() =>
        new(new Failure(FailureKind.Unreachable, "Cancelled", "", "client_cancelled", null, false));

    public Task<RootsResult> GetRootsAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(GetRootsAsync), null, null));
        EnterBrowseCall();
        try
        {
            if (RootsResults.Count > 0) return Task.FromResult(RootsResults.Dequeue());
            return Task.FromResult<RootsResult>(new RootsResult.Ok(Roots.ToList()));
        }
        finally { LeaveBrowseCall(); }
    }

    public async Task<ListingResult> BrowseAsync(string path, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(BrowseAsync), null, null) { Path = path });
        EnterBrowseCall();
        try
        {
            if (BeforeBrowseAnswer is not null) await BeforeBrowseAnswer(path).ConfigureAwait(false);
            // Like the real link: a cancelled call answers quickly with Failed(client_cancelled).
            if (ct.IsCancellationRequested) return Cancelled();
            if (ListingResults.Count > 0) return ListingResults.Dequeue();
            if (Listings.TryGetValue(path, out var listing)) return new ListingResult.Ok(listing);
            return new ListingResult.Failed(new Failure(FailureKind.FolderNotFound, "Folder not found", path, "client_test", null, false));
        }
        finally { LeaveBrowseCall(); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ActionResult NextActionResult()
    {
        if (ActionResults.Count > 0) return ActionResults.Dequeue();
        return Snapshot is not null ? new ActionResult.Applied(Snapshot) : new ActionResult.NotSent(NotSentReason.NoSession);
    }

    public int CallCount(string method) => Calls.Count(c => c.Method == method);
}
