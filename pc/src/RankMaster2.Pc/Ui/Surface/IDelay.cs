namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// "Wait this long" — used for exactly one thing, the ~100 ms select cue (plan § 1.2, § 3.1: "the
/// cue plays first, then the vote is sent"). Abstracted so a test can control the cue's completion
/// by hand (to exercise "Esc during the cue", plan § 3.1) instead of sleeping for real.
/// </summary>
public interface IDelay
{
    Task Wait(TimeSpan span, CancellationToken ct = default);
}

public sealed class SystemDelay : IDelay
{
    public static readonly SystemDelay Instance = new();
    public Task Wait(TimeSpan span, CancellationToken ct = default) => Task.Delay(span, ct);
}

/// <summary>Test double: every call to <see cref="Wait"/> returns a task the test completes by hand
/// via <see cref="Release"/>, so a scripted test can land an intent squarely inside the cue window.</summary>
public sealed class FakeDelay : IDelay
{
    private readonly List<TaskCompletionSource> _pending = new();

    public Task Wait(TimeSpan span, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_pending) _pending.Add(tcs);
        return tcs.Task;
    }

    /// <summary>Completes the oldest still-pending wait. Throws if none is pending.</summary>
    public void Release()
    {
        TaskCompletionSource tcs;
        lock (_pending)
        {
            if (_pending.Count == 0) throw new InvalidOperationException("No pending FakeDelay.Wait to release.");
            tcs = _pending[0];
            _pending.RemoveAt(0);
        }
        tcs.TrySetResult();
    }

    public int PendingCount { get { lock (_pending) return _pending.Count; } }
}

/// <summary>Test double for when the cue's exact timing does not matter to the test: every wait
/// completes immediately.</summary>
public sealed class ImmediateDelay : IDelay
{
    public Task Wait(TimeSpan span, CancellationToken ct = default) => Task.CompletedTask;
}
