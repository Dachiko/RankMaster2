// Who owns a still's StillLease, and for how long.
//
// Two properties, and a fix for either that breaks the other is not a fix:
//
//   * A pane that is KEPT (same id, same mediaVersion, across a vote or a pane-size correction) must
//     end up owning a lease that is still alive, so nothing can ever be painted out of a buffer that
//     has been freed. AUDIT2.md section 1.1: the pane was carried forward by
//     `old.WithGeneration(pairSeq)`, a record `with` that copies the same StillLease object -- one
//     PaneControl.Render had already disposed -- and the Show()/EnsureFreshLocked that follows frees
//     the cached frame whenever it is smaller than the pane it is now being asked to fill. That
//     includes StillSource.SetPaneSize's own correction of an already-visible pane the moment the real
//     window size is known, which used to be deferred to the next vote and left the very first pair
//     permanently soft.
//   * A lease NOBODY consumes must be disposed at once. IStillSource.Show raises Changed for both
//     ids with a fresh lease each, including for a pane that is already Ready; dropped rather than
//     disposed, that lease and the DecodeBudget bytes behind it live until the process exits (the
//     earlier H8 finding: about 124 votes before every decode started failing).
//
// The first test uses a REAL StillSource, a real window and the real PaneControl, because a fake
// still source never frees a frame and so cannot prove anything about a freed one.
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RankMaster2.Pc.App;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

public class PaneLeaseLifetimeTests
{
    // ---- the reused pane, against a real StillSource that really frees ----------------------------

    [AvaloniaFact]
    public async Task A_pane_kept_for_the_next_pair_is_still_backed_by_live_pixels()
    {
        var folder = MediaFolder.New();
        try
        {
            MediaFolder.WriteFile(folder, "a.bmp");
            MediaFolder.WriteFile(folder, "b.bmp");
            MediaFolder.WriteFile(folder, "c.bmp");

            // 1000 x 1000 sources are the point: fitted to StillSource's 960 x 1080 default -- all it
            // has until RankView.Loaded/SizeChanged reports the real pane -- the first pair decodes to
            // 960 x 960, which does NOT cover a pane any bigger, so the real size arriving releases
            // those frames and queues a correction.
            var budget = new DecodeBudget(64L * 1024 * 1024);
            var decoder = new SizedDecoder(budget)
                .WithSource("a.bmp", 1000, 1000).WithSource("b.bmp", 1000, 1000).WithSource("c.bmp", 1000, 1000);
            await using var stills = new StillSource(decoder);

            var first = SnapshotBuilder.Ranking(folder: folder, leftId: "a.bmp", rightId: "b.bmp", pairSeq: 1);
            var link = new FakeSessionLink { Snapshot = first };
            var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
            var coordinator = new RankCoordinator(link, stills, null, clock, new AvaloniaUiThread(), new ImmediateDelay(),
                new LastFolderStore(Path.Combine(MediaFolder.New(), "last-folder.txt")));

            var root = new UiRoot(coordinator);
            // Deliberately larger than 1920 x 1080: on a monitor this size each pane is bigger than
            // 960 x 1080, which is exactly the condition AUDIT2.md section 1.1 says his monitor meets.
            var window = new Window { Content = root, Width = 2600, Height = 1400 };
            window.Show();

            link.OpenResults.Enqueue(new OpenResult.Opened(first, false));
            Assert.True(await coordinator.OpenFolderAsync(folder));

            // The pane really is bigger than the size the first pair was decoded at -- without this
            // there would be nothing for StillSource.SetPaneSize to correct, and nothing to prove.
            // RankView (and so PaneControl) is built lazily once the pair is Ready, so this also waits
            // for that to happen.
            Assert.True(await Pump(() =>
            {
                var pane = FindDescendant<PaneControl>(root, "LeftPane");
                return pane is not null && (pane.Bounds.Width > 960 || pane.Bounds.Height > 1080);
            }), "the test window never laid out bigger than the placeholder pane size");

            // StillSource.SetPaneSize now corrects an already-visible pane on its own, the moment
            // RankView.Loaded/SizeChanged reports the real size -- it no longer waits for a vote to
            // notice, which used to leave the very first pair permanently soft. Exactly when that
            // correction lands relative to this test's own layout pump is a race this test does not
            // control, so what it can assert is the outcome: both panes end up Ready, at the real pane
            // size rather than stuck at the 960 x 960 placeholder, with a lease pointing at live
            // pixels. (Had the correction instead raced a repaint into a freed buffer -- AUDIT2.md
            // section 1.1 -- the ObjectDisposedException would have come out of one of the Pump calls
            // above or below, since UiRoot repaints inline on the UI thread.)
            Assert.True(await Pump(() => coordinator.Rank.Left?.Kind == PaneKind.Ready
                                      && coordinator.Rank.Right?.Kind == PaneKind.Ready
                                      && coordinator.Rank.Left.Lease!.Frame.Width > 960),
                "the first pair was never corrected to the real pane size");

            var settledFrame = coordinator.Rank.Left!.Lease!.Frame;
            Assert.NotEqual(IntPtr.Zero, settledFrame.Pixels);
            clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));

            // The next pair keeps a.bmp and brings c.bmp in: the ordinary case in pairwise ranking.
            // a.bmp is already at the right size by now, so this Show() should need no further release
            // -- but the property that matters, whenever a release does race a kept pane, is that the
            // pane's own lease is never left pointing at freed memory. Before the fix this threw
            // ObjectDisposedException out of BudgetedBuffer, inside a repaint that had already copied
            // from the same pointer.
            link.ActionResults.Enqueue(new ActionResult.Applied(
                SnapshotBuilder.Ranking(folder: folder, leftId: "a.bmp", rightId: "c.bmp", pairSeq: 2)));

            // Any exception from the repaint this vote provokes comes out here, because UiRoot's Post
            // runs inline when it is already on the UI thread.
            await coordinator.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var kept = coordinator.Rank.Left!;
            Assert.Equal("a.bmp", kept.Id);
            Assert.Equal(PaneKind.Ready, kept.Kind);
            Assert.Equal(2, kept.Generation);
            Assert.NotNull(kept.Lease);
            Assert.NotEqual(IntPtr.Zero, kept.Lease!.Frame.Pixels);
            Assert.Same(settledFrame, kept.Lease.Frame); // already the right size: no release was needed this time

            Assert.True(await Pump(() => coordinator.Rank.Right?.Kind == PaneKind.Ready), "c.bmp never decoded");
        }
        finally
        {
            MediaFolder.Cleanup(folder);
        }
    }

    /// <summary>The arithmetic behind "it depends on his monitor", with no decode in it: the frame the
    /// first pair leaves behind is decoded at <c>StillSource</c>'s 960 x 1080 default, and every pane
    /// bigger than that in either direction makes it too small to reuse.</summary>
    [Theory]
    [InlineData(960, 1080, true)]    // exactly 1920 x 1080, split in two: safe
    [InlineData(1280, 1440, false)]  // 2560 x 1440
    [InlineData(1920, 2160, false)]  // 3840 x 2160
    [InlineData(1440, 1620, false)]  // 1920 x 1080 at 150% scaling
    public void A_frame_decoded_at_the_default_pane_size_covers_only_a_pane_no_bigger(int paneW, int paneH, bool covers)
    {
        // A 4000 x 3000 photograph, fitted to the 960 x 1080 default: 1440 x 1080.
        var (frameW, frameH) = DecodeGeometry.Fit(4000, 3000, 960, 1080);
        Assert.Equal(covers, DecodeGeometry.FrameCovers(frameW, frameH, 4000, 3000, paneW, paneH));
    }

    // ---- the lease nobody consumes ---------------------------------------------------------------

    [Fact]
    public async Task A_lease_raised_for_a_pane_that_is_already_showing_it_is_disposed_at_once()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "b.jpg", pairSeq: 1) };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var delay = new FakeDelay();
        var c = new RankCoordinator(link, stills, new FakeVideoSurfaceFactory(), clock, new SynchronousUiThread(), delay,
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-lease-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        var frameA = NewFrame("a.jpg");
        var frameB = NewFrame("b.jpg");
        var leaseA1 = NewLease(frameA);
        var leaseB1 = NewLease(frameB);
        stills.SetState("a.jpg", new StillState.Ready(leaseA1));
        stills.SetState("b.jpg", new StillState.Ready(leaseB1));

        var first = link.Snapshot!;
        link.OpenResults.Enqueue(new OpenResult.Opened(first, false));
        Assert.True(await c.OpenFolderAsync(first.Folder));
        Assert.Equal(PaneKind.Ready, c.Rank.Left!.Kind);
        Assert.Same(leaseA1, c.Rank.Left.Lease);

        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));

        // The next pair keeps a.jpg on the left (kept) and brings c.jpg in on the right (Waiting).
        // Show() raises Changed(a.jpg, Ready(<a fresh lease on the same frame>)) as the real source does.
        var leaseA2 = NewLease(frameA);
        stills.SetState("a.jpg", new StillState.Ready(leaseA2));
        stills.SetState("c.jpg", new StillState.Pending());
        link.ActionResults.Enqueue(new ActionResult.Applied(
            SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "c.jpg", pairSeq: 2)));

        var vote = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release();
        await vote;

        Assert.Single(stills.ShowCalls, s => s.LeftId == "a.jpg" && s.RightId == "c.jpg");
        Assert.Equal(PaneKind.Ready, c.Rank.Left!.Kind);     // kept, as designed
        Assert.Same(leaseA1, c.Rank.Left.Lease);             // and it is the lease it was already holding
        Assert.False(IsDisposed(leaseA1), "the kept pane's own lease must stay alive: it is what the next repaint copies from");

        // Nobody else owns the lease Show raised for the kept pane, so the coordinator disposes it
        // itself -- the DecodeBudget bytes behind it are not held twice.
        Assert.True(IsDisposed(leaseA2), "a lease no pane consumed was dropped instead of disposed");
        Assert.Equal(2, Refs(frameA)); // the cache's own reference, plus the kept pane's live lease
    }

    /// <summary>A frame the pane has finished with (its pair is gone) leaves nothing behind: the
    /// coordinator disposes the outgoing lease, and with the cache's own reference already released
    /// the buffer is freed on the spot.</summary>
    [Fact]
    public async Task A_pane_replaced_by_a_different_picture_disposes_the_lease_it_was_holding()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "b.jpg", pairSeq: 1) };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var delay = new FakeDelay();
        var c = new RankCoordinator(link, stills, new FakeVideoSurfaceFactory(), clock, new SynchronousUiThread(), delay,
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-lease-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        var frameA = NewFrame("a.jpg");
        var leaseA = NewLease(frameA);
        stills.SetState("a.jpg", new StillState.Ready(leaseA));
        stills.SetState("b.jpg", new StillState.Ready(NewLease(NewFrame("b.jpg"))));

        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot!, false));
        Assert.True(await c.OpenFolderAsync(link.Snapshot!.Folder));
        Assert.Same(leaseA, c.Rank.Left!.Lease);
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));

        stills.SetState("d.jpg", new StillState.Pending());
        stills.SetState("e.jpg", new StillState.Pending());
        link.ActionResults.Enqueue(new ActionResult.Applied(
            SnapshotBuilder.Ranking(leftId: "d.jpg", rightId: "e.jpg", pairSeq: 2)));

        var vote = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release();
        await vote;

        Assert.Equal("d.jpg", c.Rank.Left!.Id);
        Assert.Null(c.Rank.Left.Lease);
        Assert.True(IsDisposed(leaseA), "the outgoing pane's lease leaked: nothing else will ever dispose it");
        Assert.Equal(1, Refs(frameA)); // only the cache's own reference is left
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    private static readonly DecodeBudget Budget = new(64L * 1024 * 1024);

    private static StillFrame NewFrame(string id)
    {
        var buffer = Budget.Allocate(16 * 16 * 4);
        var ctor = typeof(StillFrame).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return (StillFrame)ctor.Invoke(new object[] { id, buffer, 16, 16, 16, 16, false });
    }

    private static StillLease NewLease(StillFrame frame)
    {
        var lease = typeof(StillFrame).GetMethod("Lease", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (StillLease)lease.Invoke(frame, null)!;
    }

    private static bool IsDisposed(StillLease lease)
    {
        var f = typeof(StillLease).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)f.GetValue(lease)! != 0;
    }

    private static int Refs(StillFrame frame)
    {
        var f = typeof(StillFrame).GetField("_refs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)f.GetValue(frame)!;
    }

    /// <summary>Runs the dispatcher until <paramref name="condition"/> holds -- a real decode lands on
    /// one of StillSource's own worker threads and is marshalled back by AvaloniaUiThread.</summary>
    private static async Task<bool> Pump(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static T? FindDescendant<T>(Control root, string? name = null) where T : Control
    {
        if (root is T match && (name is null || root.Name == name)) return match;
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control c)
            {
                var found = FindDescendant<T>(c, name);
                if (found is not null) return found;
            }
        }
        return null;
    }
}
