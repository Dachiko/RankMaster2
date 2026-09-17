namespace RankMaster2.Pc.App;

using Avalonia.Controls;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Views;

/// <summary>
/// A-startup-and-shell.md § 3.1: constructs parts B, C, D and (once it lands) E's <c>UiRoot</c> —
/// constructors only, no network, no native load (§ 8 test 13). Called from
/// <c>App.OnFrameworkInitializationCompleted</c>, before the shell is shown.
/// </summary>
public static class Composition
{
    public static CompositionResult Build()
    {
        var link = SessionLinkFactory.Create(new LinkOptions());
        var probe = new RankMaster2.Pc.Stills.MediaProbe(); // Stills' folder-level IMediaProbe (C § 2.2) — Video/ has its own MediaProbe too (D's per-file classifier), hence the full name

        var uiThread = new AvaloniaUiThread();
        var backend = new LibVlcBackend();
        var videoEngine = new VideoEngine(backend, uiThread, new VideoOptions(), LibVlcLayout.NativeDir);

        var gate = new VideoEngineGate(videoEngine, LibVlcLayout.NativeDir);
        var wakingLink = new WakingSessionLink(link, probe, gate);

        var stillSource = new StillSource(new DecodeBudget(512L * 1024 * 1024));

        // Part E's surface. The coordinator decides everything; UiRoot only draws it and turns
        // keystrokes into intents. One AvaloniaUiThread serves both it and the video engine - the
        // two parts declare their own IUiThread rather than sharing a seam, so this class satisfies
        // both, which is cheaper than an adapter that would do nothing but forward.
        var coordinator = new RankCoordinator(
            wakingLink,
            stillSource,
            videoEngine,
            new SystemClock(),
            uiThread,
            new SystemDelay(),
            new LastFolderStore());

        var root = new UiRoot(coordinator);
        var lifetime = new AppLifetime(wakingLink);
        root.QuitRequested += () => _ = lifetime.QuitNow();

        // AUDIT2.md § 3.1: RankCoordinator.InitializeAsync is the only place that loads the last
        // folder (Start.LastFolder = _folderStore.Load()) and the only place that raises Changed
        // once ConnectAsync finishes — it is not the same thing as Link.ConnectAsync, which is all
        // MainWindow.OnOpened called before this fix. Handing out the coordinator's own delegate
        // (rather than the coordinator itself) keeps CompositionResult's surface exactly as narrow
        // as it was: one more thing App/ can call, not a new type App/ now depends on.
        return new CompositionResult(wakingLink, stillSource, videoEngine, gate, lifetime, root, coordinator.InitializeAsync);
    }
}

/// <summary>Everything <c>App/</c> needs to hold onto after <see cref="Composition.Build"/>.</summary>
public sealed record CompositionResult(
    ISessionLink Link,
    IStillSource Stills,
    IVideoSurfaceFactory Video,
    VideoEngineGate VideoGate,
    AppLifetime Lifetime,
    Control Root,
    Func<CancellationToken, Task> InitializeAsync);
