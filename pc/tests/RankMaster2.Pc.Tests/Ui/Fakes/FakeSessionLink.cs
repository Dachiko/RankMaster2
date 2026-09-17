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

    public sealed record Call(string Method, Side? Side, long? PairSeq);
    public readonly List<Call> Calls = new();

    public Queue<ConnectResult> ConnectResults { get; } = new();
    public Queue<OpenResult> OpenResults { get; } = new();

    /// <summary>Shared by Vote/Skip/Discard/Special/Undo/Save/Refresh -- dequeued in call order.
    /// Defaults to Applied(Snapshot) (or a NotSent(NoSession) if Snapshot is null) when empty.</summary>
    public Queue<ActionResult> ActionResults { get; } = new();

    public Task<ConnectResult> ConnectAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(ConnectAsync), null, null));
        return Task.FromResult(ConnectResults.Count > 0 ? ConnectResults.Dequeue() : new ConnectResult.Connected("https://fake", false, false));
    }

    public Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(OpenAsync), null, null));
        if (OpenResults.Count > 0) return Task.FromResult(OpenResults.Dequeue());
        if (Snapshot is not null) return Task.FromResult<OpenResult>(new OpenResult.Opened(Snapshot, false));
        throw new InvalidOperationException("FakeSessionLink.OpenAsync: no scripted OpenResult and no default Snapshot set.");
    }

    public Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(VoteAsync), winner, onPairSeq));
        return Task.FromResult(NextActionResult());
    }

    public Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(SkipAsync), null, onPairSeq));
        return Task.FromResult(NextActionResult());
    }

    public Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(DiscardAsync), side, onPairSeq));
        return Task.FromResult(NextActionResult());
    }

    public Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(SpecialAsync), side, onPairSeq));
        return Task.FromResult(NextActionResult());
    }

    public Task<ActionResult> UndoAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(UndoAsync), null, null));
        return Task.FromResult(NextActionResult());
    }

    public Task<ActionResult> SaveAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(SaveAsync), null, null));
        return Task.FromResult(NextActionResult());
    }

    public Task<ActionResult> RefreshAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(RefreshAsync), null, null));
        return Task.FromResult(NextActionResult());
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        Calls.Add(new Call(nameof(CloseAsync), null, null));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ActionResult NextActionResult()
    {
        if (ActionResults.Count > 0) return ActionResults.Dequeue();
        return Snapshot is not null ? new ActionResult.Applied(Snapshot) : new ActionResult.NotSent(NotSentReason.NoSession);
    }

    public int CallCount(string method) => Calls.Count(c => c.Method == method);
}
