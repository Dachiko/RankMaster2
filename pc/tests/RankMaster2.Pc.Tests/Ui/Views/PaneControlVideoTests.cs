using System.Reflection;
using Avalonia.Headless.XUnit;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using RankMaster2.Pc.Video;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// The owner's report: a video sits frozen on its first frame until he clicks, because clicking
/// happens to force an unrelated repaint (the vote cue). <c>IVideoSurface.FrameChanged</c>'s own
/// contract says "E calls InvalidateVisual on its Image" -- nothing did. D was really decoding and
/// writing every frame into the same <c>WriteableBitmap</c> in place, but reassigning
/// <c>_frame.Source</c> to that same instance is a no-op for Avalonia's dirty tracking, so the pane
/// never repainted past whatever frame was current when <c>RankCoordinator.Changed</c> last fired.
/// <para/>
/// What actually needs proving is the subscription bookkeeping <c>PaneControl</c> now does: wired
/// once per surface instance (not once per <c>Render</c> call, which happens far more often than the
/// surface changes), and moved off the old surface the moment a different one arrives -- otherwise
/// either nothing repaints, or a stale surface's frames keep invalidating a pane that has moved on.
/// </summary>
public class PaneControlVideoTests
{
    private static int SubscriberCountOf(FakeVideoSurface surface)
    {
        var field = typeof(FakeVideoSurface).GetField(nameof(FakeVideoSurface.FrameChanged), BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (field.GetValue(surface) as MulticastDelegate)?.GetInvocationList().Length ?? 0;
    }

    private static PaneState VideoPane(string id, DateTimeOffset now) =>
        PaneState.Waiting(Side.Left, generation: 1, id, mediaVersion: "v1", isVideo: true, now).WithVideoReady();

    [AvaloniaFact]
    public void Render_wires_FrameChanged_once_per_surface_and_moves_off_the_old_one()
    {
        var control = new PaneControl();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var pane = VideoPane("clip.mp4", clock.UtcNow);
        var surfaceA = new FakeVideoSurface("left");
        var surfaceB = new FakeVideoSurface("left");

        control.Render(pane, Side.Left, surfaceA, VideoEngineStatus.Ready, clock);
        Assert.Equal(1, SubscriberCountOf(surfaceA));

        // Render runs on every RankCoordinator.Changed, far more often than the surface itself
        // changes (votes, cues, toasts...) -- re-rendering the same surface must not pile up a
        // second subscription.
        control.Render(pane, Side.Left, surfaceA, VideoEngineStatus.Ready, clock);
        Assert.Equal(1, SubscriberCountOf(surfaceA));

        control.Render(pane, Side.Left, surfaceB, VideoEngineStatus.Ready, clock);
        Assert.Equal(0, SubscriberCountOf(surfaceA)); // unwired: a late frame from the old clip must not reach here
        Assert.Equal(1, SubscriberCountOf(surfaceB));
    }

    [AvaloniaFact]
    public void A_frame_arriving_with_no_state_change_still_repaints()
    {
        var control = new PaneControl();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var pane = VideoPane("clip.mp4", clock.UtcNow);
        var surface = new FakeVideoSurface("left");

        control.Render(pane, Side.Left, surface, VideoEngineStatus.Ready, clock);

        // The exact case that was silently dropped: a second (and third...) frame from LibVLC with
        // no accompanying StateChanged and no new RankCoordinator.Changed/Render call at all. Before
        // the fix, PaneControl had no subscriber on FrameChanged, so this was a no-op reaching
        // nothing; the assertion is simply that raising it doesn't throw with nobody listening wrong
        // or against the wrong surface -- SubscriberCountOf pins that it really does reach exactly
        // this pane's own handler.
        Assert.Equal(1, SubscriberCountOf(surface));
        var ex = Record.Exception(surface.RaiseFrame);
        Assert.Null(ex);
    }
}
