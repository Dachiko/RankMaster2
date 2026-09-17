// AUDIT2.md § 4.5: "everything in RankCoordinator that touches RankModel goes through IUiThread
// first" is what IUiThread's own documentation promises, and RunAction did not keep it -- every
// await used ConfigureAwait(false), so ApplyResult, ApplySnapshotSync, SetPanes, SetSnapshot and
// ExitBusy all ran on whichever thread-pool thread the link call finished on, while
// RankView.Refresh read the same fields on the UI thread across no barrier at all (Busy is a plain
// non-volatile bool).
//
// No coordinator test could see that, because SynchronousUiThread runs on the caller's thread by
// design: with it, "marshalled" and "not marshalled" look identical. These tests use a real,
// separate UI thread and a link that completes on the thread pool, as a real HTTP call does.
using System.Collections.Concurrent;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

public class UiThreadDisciplineTests
{
    [Fact]
    public async Task An_action_whose_result_arrives_off_the_UI_thread_is_still_applied_on_it()
    {
        using var ui = new SingleThreadUiThread();
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(pairSeq: 1), CompleteOffThread = true };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var c = new RankCoordinator(link, stills, new FakeVideoSurfaceFactory(), clock, ui, new ImmediateDelay(),
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-uithread-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        var changedOn = new ConcurrentBag<int>();
        c.Changed += () => changedOn.Add(Environment.CurrentManagedThreadId);

        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot!, false));
        Assert.True(await RunOnUiThread(ui, () => c.OpenFolderAsync(link.Snapshot!.Folder)));

        await ui.OnThread(() =>
        {
            c.Rank.SetPanes(c.Rank.Left! with { Kind = PaneKind.Ready }, c.Rank.Right! with { Kind = PaneKind.Ready }, c.Rank.PairArrivedAt);
            clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));
        });

        link.ActionResults.Enqueue(new ActionResult.Applied(SnapshotBuilder.Ranking(leftId: "c.jpg", rightId: "d.jpg", pairSeq: 2)));
        await RunOnUiThread(ui, () => c.OnCompareKeyDown(UiKey.Right, UiModifiers.None));

        // The vote really did come back on a thread-pool thread (otherwise the rest proves nothing).
        Assert.Equal(1, link.CallCount(nameof(ISessionLink.VoteAsync)));

        // Everything the result did to the model happened on the one UI thread: the new snapshot was
        // applied there (Show is called from ApplySnapshotSync), and every repaint was raised there.
        Assert.NotEmpty(stills.ShowThreadIds);
        Assert.All(stills.ShowThreadIds, id => Assert.Equal(ui.ThreadId, id));
        Assert.NotEmpty(changedOn);
        Assert.All(changedOn, id => Assert.Equal(ui.ThreadId, id));

        // And the busy flag Views reads with no barrier is back down, set on that same thread.
        Assert.Equal(2, await ui.OnThread(() => c.Rank.CurrentPairSeq));
        Assert.False(await ui.OnThread(() => c.Rank.Busy));
    }

    [Fact]
    public async Task A_fatal_refusal_that_arrives_off_the_UI_thread_returns_to_the_start_screen_on_it()
    {
        using var ui = new SingleThreadUiThread();
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(pairSeq: 1), CompleteOffThread = true };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var c = new RankCoordinator(link, stills, new FakeVideoSurfaceFactory(), clock, ui, new ImmediateDelay(),
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-uithread-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot!, false));
        Assert.True(await RunOnUiThread(ui, () => c.OpenFolderAsync(link.Snapshot!.Folder)));
        await ui.OnThread(() =>
        {
            c.Rank.SetPanes(c.Rank.Left! with { Kind = PaneKind.Ready }, c.Rank.Right! with { Kind = PaneKind.Ready }, c.Rank.PairArrivedAt);
            clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));
        });

        // Recording starts here, so that only what the action does is measured -- the press itself is
        // Views' call and is always made on the UI thread.
        var changedOn = new ConcurrentBag<int>();
        c.Changed += () => changedOn.Add(Environment.CurrentManagedThreadId);

        var fatal = new Failure(FailureKind.PairingLost, "This PC is no longer paired", "Restart the server.", "token_revoked", null, Fatal: true);
        link.ActionResults.Enqueue(new ActionResult.Refused(fatal, null));
        await RunOnUiThread(ui, () => c.OnCompareKeyDown(UiKey.Right, UiModifiers.None));

        // Screen, the start screen's message and Busy all changed after a call that finished on the
        // thread pool; every one of those writes, and every repaint they asked for, was on the UI thread.
        Assert.Equal(AppScreen.Start, await ui.OnThread(() => c.Screen));
        Assert.Contains("no longer paired", await ui.OnThread(() => c.Start.BoxText ?? ""));
        Assert.False(await ui.OnThread(() => c.Rank.Busy));
        Assert.NotEmpty(changedOn);
        Assert.All(changedOn, id => Assert.Equal(ui.ThreadId, id));
    }

    /// <summary>Starts <paramref name="work"/> on the UI thread -- as Views always does -- and awaits
    /// the task it returns from wherever that task completes.</summary>
    private static async Task<T> RunOnUiThread<T>(SingleThreadUiThread ui, Func<Task<T>> work)
    {
        Task<T>? started = null;
        await ui.OnThread(() => { started = work(); });
        return await started!;
    }

    private static async Task RunOnUiThread(SingleThreadUiThread ui, Func<Task> work)
    {
        Task? started = null;
        await ui.OnThread(() => { started = work(); });
        await started!;
    }
}
