namespace RankMaster2.Pc.Video;

/// <summary>
/// The one dependency <c>Video/</c> has on "there is a UI thread", kept as an interface so the state
/// machine (<see cref="VideoSurface"/>) runs under xunit with a synchronous implementation and no
/// Avalonia dispatcher. Production gets an Avalonia-backed implementation from part A's composition
/// root; this file does not construct one.
/// </summary>
public interface IUiThread
{
    /// <summary>Marshals <paramref name="action"/> to the UI thread. May run synchronously if already
    /// on it, exactly like a normal dispatcher.</summary>
    void Post(Action action);

    bool IsOnThread { get; }
}
