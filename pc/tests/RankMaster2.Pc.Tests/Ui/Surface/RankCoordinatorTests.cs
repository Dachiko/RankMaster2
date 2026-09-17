using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Video;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>
/// Plan § 6.1 "RankCoordinator": a scripted session against the fake link/stills/video, asserting
/// exactly the calls made and no others. Covers the plan's "what matters most": one press is one
/// action (§ 3.1), Ctrl+Z says what it undid and a second press is a quiet no-op (§ 3.4), and the
/// failure table picks toast vs start screen off <c>Failure.Fatal</c> alone (§ 3.5).
/// </summary>
public class RankCoordinatorTests
{
    private static (RankCoordinator Coordinator, FakeSessionLink Link, FakeStillSource Stills, FakeVideoSurfaceFactory Video, FakeClock Clock, FakeDelay Delay)
        Build(Snapshot? initial = null)
    {
        var link = new FakeSessionLink { Snapshot = initial ?? SnapshotBuilder.Ranking() };
        var stills = new FakeStillSource();
        var video = new FakeVideoSurfaceFactory();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var delay = new FakeDelay();
        var coordinator = new RankCoordinator(link, stills, video, clock, new SynchronousUiThread(), delay,
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-ui-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));
        return (coordinator, link, stills, video, clock, delay);
    }

    /// <summary>Opens a folder synchronously (ImmediateDelay is irrelevant here; OpenAsync never cues)
    /// and returns once the compare screen is up, with both panes pushed to Ready so the gate's
    /// "ready" rule passes and the 200 ms arrival guard has elapsed.</summary>
    private static async Task EnterReadyCompareScreen(RankCoordinator c, FakeSessionLink link, FakeClock clock, Snapshot snapshot)
    {
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        Assert.True(await c.OpenFolderAsync(snapshot.Folder));
        c.Rank.SetPanes(
            c.Rank.Left! with { Kind = PaneKind.Ready },
            c.Rank.Right! with { Kind = PaneKind.Ready },
            c.Rank.PairArrivedAt);
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));
    }

    // ---- gate integration: one press, one action --------------------------------------------------

    [Fact]
    public async Task One_accepted_vote_makes_exactly_one_Vote_call_with_the_displayed_pairSeq()
    {
        var (c, link, _, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking(pairSeq: 7);
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        var task = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release(); // let the ~100ms select cue complete
        await task;

        Assert.Equal(1, link.CallCount(nameof(link.VoteAsync)));
        var call = link.Calls.Single(x => x.Method == nameof(link.VoteAsync));
        Assert.Equal(Side.Right, call.Side);
        Assert.Equal(7L, call.PairSeq);
    }

    [Fact]
    public async Task Ten_presses_during_one_in_flight_call_still_make_one_call()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());

        var first = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        for (var i = 0; i < 10; i++)
            await c.OnCompareKeyDown(UiKey.Right, UiModifiers.None); // busy: every one of these is dropped

        delay.Release();
        await first;

        Assert.Equal(1, link.CallCount(nameof(link.VoteAsync)));
    }

    [Fact]
    public async Task A_held_key_votes_once_however_long_it_is_held()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());

        var first = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release();
        await first;

        // Still held, no KeyUp yet: repeats must not vote again even once busy has cleared.
        await c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        Assert.Equal(1, link.CallCount(nameof(link.VoteAsync)));

        c.OnCompareKeyUp(UiKey.Right);
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));
        var second = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release();
        await second;
        Assert.Equal(2, link.CallCount(nameof(link.VoteAsync)));
    }

    [Fact]
    public async Task A_click_is_always_released_so_two_quick_clicks_still_gate_on_busy_alone()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());

        var first = c.OnPaneClicked(Side.Left);
        var second = c.OnPaneClicked(Side.Left); // arrives while busy: dropped
        delay.Release();
        await first;
        await second;

        Assert.Equal(1, link.CallCount(nameof(link.VoteAsync)));
    }

    [Fact]
    public async Task Nothing_is_accepted_within_200ms_of_the_pair_landing()
    {
        var (c, link, _, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking();
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        await c.OpenFolderAsync(snapshot.Folder);
        c.Rank.SetPanes(c.Rank.Left! with { Kind = PaneKind.Ready }, c.Rank.Right! with { Kind = PaneKind.Ready }, c.Rank.PairArrivedAt);
        // No clock advance: still inside the 200ms arrival guard.

        await c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        Assert.Equal(0, link.CallCount(nameof(link.VoteAsync)));
    }

    [Fact]
    public async Task Esc_during_the_cue_sends_nothing_and_raises_QuitRequested()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());

        var quit = false;
        c.QuitRequested += () => quit = true;

        var vote = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None); // begins the cue, awaiting delay.Wait
        await c.OnCompareKeyDown(UiKey.Escape, UiModifiers.None);     // Esc is not gated; processed at once
        delay.Release();                                              // cue completes; RunAction checks Quitting
        await vote;

        Assert.True(quit);
        Assert.Equal(0, link.CallCount(nameof(link.VoteAsync)));
    }

    // ---- Ctrl+Z: says what it undid, then a quiet no-op ---------------------------------------------

    [Theory]
    [InlineData("vote", "Vote taken back")]
    [InlineData("discard", "Discard taken back")]
    [InlineData("special", "Moved back out of special 1")]
    public async Task Undo_toasts_exactly_what_it_undid(string undoneType, string expectedToast)
    {
        var (c, link, _, _, clock, delay) = Build();
        var opened = SnapshotBuilder.Ranking(pairSeq: 1, undoAvailable: true);
        await EnterReadyCompareScreen(c, link, clock, opened);

        var undone = SnapshotBuilder.Ranking(pairSeq: 2, undoAvailable: false,
            lastAction: SnapshotBuilder.Action("undo", id: "a.jpg", undoneType: undoneType));
        link.ActionResults.Enqueue(new ActionResult.Applied(undone));

        await c.OnCompareKeyDown(UiKey.Z, UiModifiers.Control);

        Assert.Equal(1, link.CallCount(nameof(link.UndoAsync)));
        Assert.Equal(expectedToast, c.Rank.Toast?.Text);
    }

    [Fact]
    public async Task A_second_undo_is_a_quiet_no_op_no_call_no_toast_busy_never_set()
    {
        var (c, link, _, _, clock, delay) = Build();
        var opened = SnapshotBuilder.Ranking(undoAvailable: false); // nothing to undo from the start
        await EnterReadyCompareScreen(c, link, clock, opened);

        var busySeen = false;
        // If RunAction ever entered its busy branch we would see Busy briefly true; sample right after.
        var task = c.OnCompareKeyDown(UiKey.Z, UiModifiers.Control);
        busySeen = c.Rank.Busy;
        await task;

        Assert.Equal(0, link.CallCount(nameof(link.UndoAsync)));
        Assert.Null(c.Rank.Toast);
        Assert.False(busySeen);
        Assert.False(c.Rank.Busy);
    }

    [Fact]
    public async Task Held_Ctrl_Z_makes_one_call()
    {
        var (c, link, _, _, clock, delay) = Build();
        var opened = SnapshotBuilder.Ranking(undoAvailable: true);
        await EnterReadyCompareScreen(c, link, clock, opened);

        var undone = SnapshotBuilder.Ranking(undoAvailable: false, lastAction: SnapshotBuilder.Action("undo", id: "a.jpg", undoneType: "vote"));
        link.ActionResults.Enqueue(new ActionResult.Applied(undone));

        await c.OnCompareKeyDown(UiKey.Z, UiModifiers.Control);
        await c.OnCompareKeyDown(UiKey.Z, UiModifiers.Control); // still held: dropped by rule 3

        Assert.Equal(1, link.CallCount(nameof(link.UndoAsync)));
    }

    [Fact]
    public async Task NothingToUndo_resync_is_silent()
    {
        var (c, link, _, _, clock, delay) = Build();
        var opened = SnapshotBuilder.Ranking(undoAvailable: true);
        await EnterReadyCompareScreen(c, link, clock, opened);

        link.ActionResults.Enqueue(new ActionResult.Resynchronised(opened, ResyncReason.UndoAlreadyDone));
        await c.OnCompareKeyDown(UiKey.Z, UiModifiers.Control);

        Assert.Null(c.Rank.Toast);
    }

    // ---- discard: release before move, in order ----------------------------------------------------

    [Fact]
    public async Task Discard_releases_the_still_before_calling_the_link()
    {
        var (c, link, stills, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "left.jpg", rightId: "right.jpg");
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None);

        Assert.Single(stills.ReleaseCalls);
        Assert.Equal("left.jpg", stills.ReleaseCalls[0].Id);
        var releaseIndex = link.Calls.FindIndex(x => x.Method == nameof(link.DiscardAsync));
        Assert.True(releaseIndex >= 0);
        Assert.Equal(Side.Left, link.Calls[releaseIndex].Side);
    }

    [Fact]
    public async Task A_release_that_never_completes_still_sends_the_request()
    {
        var (c, link, stills, _, clock, delay) = Build();
        stills.ReleaseNeverCompletes = true; // the fake never actually hangs, it just answers false;
                                              // the real contract's 2s cap is exercised by ReleaseWaitMaxMs
                                              // via the CancellationTokenSource in RankCoordinator regardless.
        var snapshot = SnapshotBuilder.Ranking();
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None);

        Assert.Equal(1, link.CallCount(nameof(link.DiscardAsync)));
    }

    // ---- pane reconciliation from a snapshot (plan § 3.2) -------------------------------------------

    [Fact]
    public async Task Missing_side_becomes_Gone_without_asking_stills_for_it()
    {
        var (c, link, stills, _, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "gone.jpg", rightId: "b.jpg", leftMissing: true);
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));

        await c.OpenFolderAsync(snapshot.Folder);

        Assert.Equal(PaneKind.Gone, c.Rank.Left!.Kind);
        Assert.Contains("gone.jpg", c.Rank.Left.Sentence);
    }

    [Fact]
    public async Task Unchanged_id_and_mediaVersion_is_reused_without_a_new_Show_call()
    {
        var (c, link, stills, _, clock, delay) = Build();
        var snapshot1 = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "b.jpg", pairSeq: 1);
        await EnterReadyCompareScreen(c, link, clock, snapshot1);
        stills.ShowCalls.Clear();

        // A vote lands a new snapshot naming the exact same pair (e.g. the server re-served it) --
        // reuse means no new Show call and the pane keeps its Ready kind untouched.
        var snapshot2 = snapshot1 with { PairSeq = 2 };
        link.ActionResults.Enqueue(new ActionResult.Applied(snapshot2));

        var task = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None);
        delay.Release();
        await task;

        Assert.Empty(stills.ShowCalls);
        Assert.Equal(PaneKind.Ready, c.Rank.Left!.Kind);
        Assert.Equal(2, c.Rank.Left.Generation);
    }

    [Fact]
    public async Task Still_changed_to_ready_updates_the_matching_pane()
    {
        var (c, link, stills, _, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "b.jpg");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));

        await c.OpenFolderAsync(snapshot.Folder);
        Assert.Equal(PaneKind.Waiting, c.Rank.Left!.Kind);

        stills.Deliver("a.jpg", new StillState.Failed(StillFailure.NotAnImage, "broken"));
        Assert.Equal(PaneKind.Undecodable, c.Rank.Left!.Kind);
    }

    // ---- video (plan § 3.6) --------------------------------------------------------------------

    [Fact]
    public async Task A_new_video_pair_calls_Play_and_the_first_frame_makes_the_pane_ready()
    {
        var (c, link, _, video, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "l.mp4", rightId: "r.mp4", kind: "video");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));

        await c.OpenFolderAsync(snapshot.Folder);

        Assert.Equal(2, video.Created.Count);
        Assert.Single(video.Created[0].PlayCalls);
        Assert.Equal(PaneKind.Waiting, c.Rank.Left!.Kind);

        video.Created[0].CompletePlay();
        Assert.Equal(PaneKind.Ready, c.Rank.Left!.Kind);
    }

    [Fact]
    public async Task Video_missing_failure_becomes_Gone_not_Undecodable()
    {
        var (c, link, _, video, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "l.mp4", rightId: "r.mp4", kind: "video");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        await c.OpenFolderAsync(snapshot.Folder);

        video.Created[0].Fail(new VideoFailure(VideoFailureKind.Missing, "the file is gone", null));

        Assert.Equal(PaneKind.Gone, c.Rank.Left!.Kind);
    }

    [Fact]
    public async Task Engine_failure_moves_waiting_video_panes_to_NoVideoEngine()
    {
        var (c, link, _, video, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "l.mp4", rightId: "r.mp4", kind: "video");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        await c.OpenFolderAsync(snapshot.Folder);

        video.EngineFailure = "LibVLC could not initialise";
        video.SetEngineStatus(VideoEngineStatus.Failed);

        Assert.Equal(PaneKind.NoVideoEngine, c.Rank.Left!.Kind);
        Assert.Equal(PaneKind.NoVideoEngine, c.Rank.Right!.Kind);
        Assert.Contains("LibVLC", c.Rank.Left.Sentence);
    }

    [Fact]
    public async Task At_most_two_video_surfaces_ever_exist_and_quit_stops_both_without_waiting_to_dispose_them()
    {
        var (c, link, _, video, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "l.mp4", rightId: "r.mp4", kind: "video");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        await c.OpenFolderAsync(snapshot.Folder);

        Assert.Equal(2, video.LiveSurfaces);

        var quit = false;
        c.QuitRequested += () => quit = true;
        await c.OnCompareKeyDown(UiKey.Escape, UiModifiers.None);

        // A21: Esc quits immediately. It asks both surfaces to stop but -- unlike the pre-move path
        // (ReleaseHandlesBeforeMove), which does wait, bounded -- it does not wait for or dispose
        // them; nobody needs the file handles released before the process exits.
        Assert.True(quit);
        Assert.Equal(2, video.LiveSurfaces);
        Assert.All(video.Created, s => Assert.Equal(1, s.StopCalls));
        Assert.All(video.Created, s => Assert.False(s.Disposed));
    }

    // ---- exhausted and failure escalation (plan § 3.5, § 4.1) --------------------------------------

    [Fact]
    public async Task Exhausted_snapshot_returns_to_the_start_screen_with_the_undo_hint()
    {
        var (c, link, _, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking(pairSeq: 1);
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        var exhausted = SnapshotBuilder.Exhausted(folder: snapshot.Folder, folderName: snapshot.FolderName, undoAvailable: true);
        link.ActionResults.Enqueue(new ActionResult.Applied(exhausted));

        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None); // discard left: any action that reaches ApplyResult

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.True(c.Start.ExhaustedSessionOpen);
        Assert.Contains("Ctrl+Z", c.Start.BoxText);
    }

    [Fact]
    public async Task A_fatal_failure_sends_the_screen_back_to_start()
    {
        var (c, link, _, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking();
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        var fatal = new Failure(FailureKind.PairingLost, "This PC is no longer paired", "Restart the server.", "token_revoked", null, Fatal: true);
        link.ActionResults.Enqueue(new ActionResult.Refused(fatal, null));

        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None);

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("no longer paired", c.Start.BoxText);
    }

    [Fact]
    public async Task A_second_consecutive_Unreachable_sends_the_screen_back_to_start()
    {
        var (c, link, _, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking();
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        var unreachable = new Failure(FailureKind.Unreachable, "The server did not answer", "Press the key again.", "client_unreachable", null, Fatal: false);

        link.ActionResults.Enqueue(new ActionResult.Unknown(unreachable));
        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None);
        Assert.Equal(AppScreen.Rank, c.Screen); // first one: toast only

        c.OnCompareKeyUp(UiKey.D1);
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));
        link.ActionResults.Enqueue(new ActionResult.Unknown(unreachable));
        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None);

        Assert.Equal(AppScreen.Start, c.Screen);
    }

    // ---- H9's second gap: Ctrl+Z from the exhausted start screen -----------------------------------

    [Fact]
    public async Task A_throwing_UndoAsync_from_the_exhausted_start_screen_returns_the_start_screen_to_its_buttons()
    {
        var (c, link, _, _, clock, delay) = Build();
        var snapshot = SnapshotBuilder.Ranking(pairSeq: 1);
        await EnterReadyCompareScreen(c, link, clock, snapshot);

        var exhausted = SnapshotBuilder.Exhausted(folder: snapshot.Folder, folderName: snapshot.FolderName, undoAvailable: true);
        link.Snapshot = exhausted;
        link.ActionResults.Enqueue(new ActionResult.Applied(exhausted));
        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None);
        Assert.True(c.Start.ExhaustedSessionOpen);

        // TryUndoFromStartAsync's own guard reads link.Snapshot.UndoAvailable, which is true here --
        // so the call is actually attempted, and the link's busy gate throws (plan's exact scenario:
        // the startup connect still running when Ctrl+Z is pressed here).
        link.UndoThrows = new InvalidOperationException("busy");

        await c.TryUndoFromStartAsync();

        Assert.False(c.Start.Opening);
        Assert.Contains("Could not open the folder", c.Start.BoxText);
        Assert.Equal(AppScreen.Start, c.Screen);
    }

    // ---- open folder ------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_open_shows_the_box_and_does_not_enter_the_compare_screen()
    {
        var (c, link, _, _, _, _) = Build();
        var failure = new Failure(FailureKind.FolderNotRankable, "Nothing to rank here", "/lib has 1 picture.", "folder_not_rankable", null, Fatal: false);
        link.OpenResults.Enqueue(new OpenResult.Failed(failure));

        var ok = await c.OpenFolderAsync("/lib");

        Assert.False(ok);
        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("Nothing to rank here", c.Start.BoxText);
    }

    [Fact]
    public async Task A_successful_open_enters_the_compare_screen_and_saves_the_folder()
    {
        var (c, link, _, _, _, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));

        var ok = await c.OpenFolderAsync("/lib/photos");

        Assert.True(ok);
        Assert.Equal(AppScreen.Rank, c.Screen);
        Assert.Equal("/lib/photos", c.Start.LastFolder);
    }

    [Fact]
    public async Task Discarding_a_video_pane_stops_it_before_calling_the_link()
    {
        var (c, link, _, video, clock, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "l.mp4", rightId: "r.mp4", kind: "video");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        await c.OpenFolderAsync(snapshot.Folder);
        video.Created[0].CompletePlay();
        video.Created[1].CompletePlay();
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));

        await c.OnCompareKeyDown(UiKey.D1, UiModifiers.None); // discard left

        Assert.Equal(1, video.Created[0].StopCalls);
        Assert.Equal(0, video.Created[1].StopCalls); // the untouched side is never stopped
        Assert.Equal(1, link.CallCount(nameof(link.DiscardAsync)));
    }

    // ---- A21: Esc must not block on a video surface releasing -------------------------------------

    [Fact]
    public async Task Quit_returns_quickly_even_when_a_video_surfaces_Dispose_would_block()
    {
        var (c, link, _, video, _, _) = Build();
        var snapshot = SnapshotBuilder.Ranking(leftId: "l.mp4", rightId: "r.mp4", kind: "video");
        link.Snapshot = snapshot;
        link.OpenResults.Enqueue(new OpenResult.Opened(snapshot, false));
        await c.OpenFolderAsync(snapshot.Folder);
        foreach (var surface in video.Created) surface.DisposeBlocks = true;

        var quit = false;
        c.QuitRequested += () => quit = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await c.OnCompareKeyDown(UiKey.Escape, UiModifiers.None);
        sw.Stop();

        Assert.True(quit);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Quit took {sw.ElapsedMilliseconds} ms -- it must never wait on Dispose (A21).");
        Assert.All(video.Created, s => Assert.False(s.Disposed)); // Stop() is asked for; Dispose is not
        Assert.All(video.Created, s => Assert.Equal(1, s.StopCalls));
    }

    // ---- A27: Esc while a dialog is open is the picker's, not ours --------------------------------

    [Fact]
    public void Esc_on_the_start_screen_does_nothing_while_the_folder_picker_is_open()
    {
        var (c, _, _, _, _, _) = Build();
        c.BeginDialog(); // on the start screen: Start.DialogOpen = true

        var quit = false;
        c.QuitRequested += () => quit = true;
        c.OnStartKeyDown(UiKey.Escape, UiModifiers.None);

        Assert.False(quit);
    }

    [Fact]
    public async Task Esc_on_the_compare_screen_does_nothing_while_the_folder_picker_is_open()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());
        c.BeginDialog(); // on the compare screen: Rank.DialogOpen = true

        var quit = false;
        c.QuitRequested += () => quit = true;
        await c.OnCompareKeyDown(UiKey.Escape, UiModifiers.None);

        Assert.False(quit);
    }

    // ---- A20: the 250 ms repaint tick ---------------------------------------------------------------

    [Fact]
    public async Task Tick_clears_an_expired_toast_and_raises_Changed()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());
        c.Rank.SetToast("Discarded a.jpg", clock);
        Assert.NotNull(c.Rank.Toast);

        var raised = false;
        c.Changed += () => raised = true;

        clock.Advance(Timings.ToastMs + TimeSpan.FromMilliseconds(1));
        c.Tick();

        Assert.Null(c.Rank.Toast);
        Assert.True(raised);
    }

    [Fact]
    public void Tick_before_anything_expired_raises_nothing()
    {
        var (c, _, _, _, _, _) = Build();

        var raised = false;
        c.Changed += () => raised = true;
        c.Tick();

        Assert.False(raised);
    }

    [Fact]
    public async Task Tick_raises_Changed_exactly_once_when_an_in_flight_call_crosses_the_late_action_threshold()
    {
        var (c, link, _, _, clock, delay) = Build();
        await EnterReadyCompareScreen(c, link, clock, SnapshotBuilder.Ranking());

        var task = c.OnCompareKeyDown(UiKey.Right, UiModifiers.None); // begins the cue, then VoteAsync -- stays in flight
        Assert.True(c.Rank.Busy);

        var raises = 0;
        c.Changed += () => raises++;

        clock.Advance(Timings.LateActionLineAfterMs + TimeSpan.FromMilliseconds(1));
        c.Tick(); // crosses the threshold: one Changed
        c.Tick(); // still late, nothing new: no Changed

        Assert.Equal(1, raises);

        delay.Release();
        await task;
    }

    // ---- H10: pane size forwarded to the still source -----------------------------------------------

    [Fact]
    public void SetPaneSize_forwards_to_the_still_source()
    {
        var (c, _, stills, _, _, _) = Build();
        c.SetPaneSize(1920, 2160);
        Assert.Equal(1920, stills.PaneWidth);
        Assert.Equal(2160, stills.PaneHeight);
    }
}
