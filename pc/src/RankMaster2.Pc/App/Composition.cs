namespace RankMaster2.Pc.App;

using Avalonia.Controls;
using RankMaster2.Pc.App.Seams;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;

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

        // TEMPORARY: part E's Ui/UiRoot has not landed in this worktree (see PlaceholderRoot's own
        // header). Swap this for `new UiRoot(wakingLink, stillSource, videoEngine, AppInfo.Version)`
        // the day it does; nothing else here changes.
        var root = new PlaceholderRoot(AppInfo.Version);
        var lifetime = new AppLifetime(wakingLink);
        root.QuitRequested += (_, _) => _ = lifetime.QuitNow();

        return new CompositionResult(wakingLink, stillSource, videoEngine, gate, lifetime, root);
    }
}

/// <summary>Everything <c>App/</c> needs to hold onto after <see cref="Composition.Build"/>.</summary>
public sealed record CompositionResult(
    ISessionLink Link,
    IStillSource Stills,
    IVideoSurfaceFactory Video,
    VideoEngineGate VideoGate,
    AppLifetime Lifetime,
    Control Root);
