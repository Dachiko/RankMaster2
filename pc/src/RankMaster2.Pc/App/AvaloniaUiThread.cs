namespace RankMaster2.Pc.App;

using Avalonia.Threading;
using RankMaster2.Pc.Video;

/// <summary>D-video.md's <see cref="IUiThread"/>, given its one production implementation here —
/// part A's composition root is the only place that is allowed to know Avalonia's dispatcher
/// exists (D-video.md § 4: Video/ takes it as an interface precisely so it never has to).</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public bool IsOnThread => Dispatcher.UIThread.CheckAccess();
}
