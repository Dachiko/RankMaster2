namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// "Run this on the UI thread." B, C and D's callbacks and completions may arrive on any thread
/// (plan § 2.3); everything in <see cref="RankCoordinator"/> that touches <see cref="RankModel"/> or
/// <see cref="StartModel"/> goes through this first. Production posts to Avalonia's dispatcher;
/// tests run the action inline so a whole scripted session executes synchronously.
/// <para/>
/// <see cref="Post"/> is fire-and-forget, which is all a callback from C or D needs. An action path
/// needs more: it has to know the model has been updated before it goes on to the next step, so it
/// uses <see cref="InvokeAsync(Action)"/> and awaits. AUDIT2.md § 4.5 is what happens without it --
/// <c>RankCoordinator.RunAction</c> awaited the link with <c>ConfigureAwait(false)</c> and then
/// applied the result on whatever thread-pool thread the call happened to finish on, while
/// <c>RankView.Refresh</c> read the same fields on the UI thread with no barrier between them.
/// </summary>
public interface IUiThread
{
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and completes once it has run. When
    /// the caller is already on the UI thread this completes synchronously, so awaiting it never
    /// costs a thread hop.</summary>
    Task InvokeAsync(Action action)
    {
        // Continuations run inline on completion (no RunContinuationsAsynchronously) precisely so
        // that an implementation which runs the action immediately -- Avalonia's dispatcher when it
        // is already on the UI thread, or SynchronousUiThread -- keeps the caller synchronous.
        var done = new TaskCompletionSource();
        Post(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Reads a value on the UI thread. Same rules as <see cref="InvokeAsync(Action)"/>.</summary>
    async Task<T> InvokeAsync<T>(Func<T> func)
    {
        var result = default(T)!;
        // A statement-bodied lambda on purpose: `() => result = func()` is a Func<T> (an assignment
        // has a value), which binds to THIS overload and recurses until the stack runs out.
        await InvokeAsync(() => { result = func(); }).ConfigureAwait(false);
        return result;
    }
}

/// <summary>Test double: runs the action immediately, on whatever thread called <see cref="Post"/>.
/// Exactly what the plan's tests need (§ 6.1: "runs the whole gate with a fake clock and no
/// Task.Delay") and exactly wrong for production, where it would run C/D callbacks off the UI
/// thread and race Avalonia's own dispatcher.</summary>
public sealed class SynchronousUiThread : IUiThread
{
    public void Post(Action action) => action();
}
