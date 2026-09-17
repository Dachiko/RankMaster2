namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// "Run this on the UI thread." B, C and D's callbacks and completions may arrive on any thread
/// (plan § 2.3); everything in <see cref="RankCoordinator"/> that touches <see cref="RankModel"/> or
/// <see cref="StartModel"/> goes through this first. Production posts to Avalonia's dispatcher;
/// tests run the action inline so a whole scripted session executes synchronously.
/// </summary>
public interface IUiThread
{
    void Post(Action action);
}

/// <summary>Test double: runs the action immediately, on whatever thread called <see cref="Post"/>.
/// Exactly what the plan's tests need (§ 6.1: "runs the whole gate with a fake clock and no
/// Task.Delay") and exactly wrong for production, where it would run C/D callbacks off the UI
/// thread and race Avalonia's own dispatcher.</summary>
public sealed class SynchronousUiThread : IUiThread
{
    public void Post(Action action) => action();
}
