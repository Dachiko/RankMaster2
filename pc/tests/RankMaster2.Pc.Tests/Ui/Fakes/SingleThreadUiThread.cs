using System.Collections.Concurrent;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>
/// An <see cref="IUiThread"/> with a real, single, identifiable thread behind it, so a test can ask
/// "was this touched on the UI thread?" and get a true answer -- which
/// <see cref="SynchronousUiThread"/>, whose whole point is to run on the caller's thread, cannot
/// give. Behaves like Avalonia's dispatcher: running inline when the caller is already on the
/// thread, queueing otherwise.
/// </summary>
public sealed class SingleThreadUiThread : IUiThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SingleThreadUiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-ui-thread" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public bool IsOnThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void Post(Action action)
    {
        if (IsOnThread) action();
        else _queue.Add(action);
    }

    /// <summary>Runs on this thread and completes when it has run. Deliberately NOT called
    /// <c>InvokeAsync</c>: a method of that name and signature on the class would implement
    /// <see cref="IUiThread"/>'s member rather than forward to its default body, and call itself.</summary>
    public Task OnThread(Action action) => ((IUiThread)this).InvokeAsync(action);

    public Task<T> OnThread<T>(Func<T> func) => ((IUiThread)this).InvokeAsync(func);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch { /* a test asserts on what was recorded, not on this thread's survival */ }
        }
    }
}
