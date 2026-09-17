// H8 regression test. IStillSource.Show raises Changed for BOTH ids synchronously with a fresh
// lease each (StillSource.RaiseChanged -> StateOfLocked -> frame.Lease()), even for a pane that is
// REUSED (same id, same mediaVersion, already Ready) -- OnStillChanged only applies a delivery when
// the pane is Waiting/Refining. RankCoordinator.OnStillChanged now disposes a lease it does not
// consume, so the DecodeBudget bytes behind a reused pane's redundant delivery are freed at once
// instead of leaking until the process exits (the finding: ~124 votes before every decode failed).
using System.Reflection;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using Xunit;

namespace RankMaster2.Pc.Tests.Audit;

public class AuditLeaseLeakTests
{
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

    [Fact]
    public async Task Reused_pane_disposes_the_lease_that_Show_raises_for_it()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "b.jpg", pairSeq: 1) };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var delay = new FakeDelay();
        var c = new RankCoordinator(link, stills, new FakeVideoSurfaceFactory(), clock, new SynchronousUiThread(), delay,
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-audit-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

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

        // Production: PaneControl.Render copies and disposes the lease it was handed.
        leaseA1.Dispose();
        leaseB1.Dispose();
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));

        // The next pair keeps a.jpg on the left (reused) and brings c.jpg in on the right (Waiting).
        // Show() will raise Changed(a.jpg, Ready(<fresh lease>)) as the real source does.
        var leaseA2 = NewLease(frameA);
        stills.SetState("a.jpg", new StillState.Ready(leaseA2));
        stills.SetState("c.jpg", new StillState.Pending());
        var second = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "c.jpg", pairSeq: 2);
        link.ActionResults.Enqueue(new ActionResult.Applied(second));

        var vote = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release();
        await vote;

        Assert.Single(stills.ShowCalls.Where(s => s.LeftId == "a.jpg" && s.RightId == "c.jpg"));
        Assert.Equal(PaneKind.Ready, c.Rank.Left!.Kind);     // reused, as designed
        Assert.Same(leaseA1, c.Rank.Left.Lease);             // still carries the already-disposed lease

        // H8, fixed: nobody else owns the lease Show raised for the reused pane, so the coordinator
        // disposes it itself -- the buffer behind it can be freed at once.
        Assert.True(IsDisposed(leaseA2), "H8 regressed: the coordinator no longer disposes a lease raised for a Ready pane");
        Assert.Equal(1, Refs(frameA)); // only the cache's own ref remains
    }
}
