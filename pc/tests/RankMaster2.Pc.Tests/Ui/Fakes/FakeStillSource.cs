using RankMaster2.Pc.Stills;

namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>A scriptable <see cref="IStillSource"/>. <see cref="Show"/> follows the real seam's
/// contract as documented (pc/src/RankMaster2.Pc/Stills/IStillSource.cs): it raises
/// <see cref="Changed"/> synchronously for both ids before returning, Ready if the test has already
/// primed that id via <see cref="SetState"/>, Pending otherwise. A test simulates a decode
/// completing later by calling <see cref="Deliver"/>.</summary>
public sealed class FakeStillSource : IStillSource
{
    public sealed record ShowCall(string Folder, string LeftId, string RightId);
    public sealed record ReleaseCall(string Folder, string Id);

    public readonly List<ShowCall> ShowCalls = new();
    public readonly List<ReleaseCall> ReleaseCalls = new();
    public readonly List<(string Folder, IReadOnlyList<(string, string)> Pairs, int PrefetchPairs)> WarmCalls = new();

    private readonly Dictionary<string, StillState> _states = new();

    public int PaneWidth { get; private set; } = 960;
    public int PaneHeight { get; private set; } = 1080;

    /// <summary>When true, <see cref="ReleaseAsync"/> returns false (simulates a lock the OS will
    /// not release within the caller's timeout) instead of true.</summary>
    public bool ReleaseNeverCompletes { get; set; }

    public void SetPaneSize(int widthPx, int heightPx)
    {
        if (widthPx < 16 || heightPx < 16) return;
        PaneWidth = widthPx;
        PaneHeight = heightPx;
    }

    /// <summary>The managed thread each <see cref="Show"/> arrived on. RankCoordinator calls Show
    /// from <c>ApplySnapshotSync</c>, so this is a direct record of which thread the snapshot was
    /// applied on (AUDIT2.md § 4.5).</summary>
    public readonly List<int> ShowThreadIds = new();

    public void Show(string folder, string leftId, string rightId)
    {
        ShowCalls.Add(new ShowCall(folder, leftId, rightId));
        ShowThreadIds.Add(Environment.CurrentManagedThreadId);
        Changed?.Invoke(leftId, StateOf(leftId));
        Changed?.Invoke(rightId, StateOf(rightId));
    }

    public void Warm(string folder, IReadOnlyList<(string LeftId, string RightId)> pairs, int prefetchPairs = 2) =>
        WarmCalls.Add((folder, pairs, prefetchPairs));

    public StillState StateOf(string id) => _states.TryGetValue(id, out var s) ? s : new StillState.Pending();

    public event Action<string, StillState>? Changed;

    /// <summary>Primes what <see cref="StateOf"/>/the next <see cref="Show"/> call reports for
    /// <paramref name="id"/>, without raising <see cref="Changed"/> itself.</summary>
    public void SetState(string id, StillState state) => _states[id] = state;

    /// <summary>Simulates an asynchronous decode completing: sets the state and raises
    /// <see cref="Changed"/>, exactly as the real decode queue would from a worker thread.</summary>
    public void Deliver(string id, StillState state)
    {
        _states[id] = state;
        Changed?.Invoke(id, state);
    }

    public Task<bool> ReleaseAsync(string folder, string id, CancellationToken ct)
    {
        ReleaseCalls.Add(new ReleaseCall(folder, id));
        return Task.FromResult(!ReleaseNeverCompletes);
    }

    public Task ReleaseAllAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
