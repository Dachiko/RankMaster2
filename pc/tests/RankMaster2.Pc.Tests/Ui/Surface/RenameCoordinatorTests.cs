using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Video;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>
/// § 3.13 item 2: "Coordinator tests with a fake link for every terminus." Covers the confirm →
/// start → poll → cancel/terminal flow against <see cref="FakeSessionLink"/> — the seam
/// <see cref="RenameTests"/> (Link.Tests) exercises against the real server; here it is the
/// coordinator's own state machine that is under test, with every server answer scripted.
/// </summary>
public class RenameCoordinatorTests
{
    private static (RankCoordinator Coordinator, FakeSessionLink Link, FakeClock Clock)
        Build(Snapshot? initial = null)
    {
        var link = new FakeSessionLink { Snapshot = initial ?? SnapshotBuilder.Ranking() };
        var stills = new FakeStillSource();
        var video = new FakeVideoSurfaceFactory();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var coordinator = new RankCoordinator(link, stills, video, clock, new SynchronousUiThread(), new FakeDelay(),
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-ui-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));
        return (coordinator, link, clock);
    }

    private static RenameOperation Op(string state, string phase, int done, int total, RenameOperationError? error = null) =>
        new("op-1", state, phase, done, total, "2026-09-17T18:00:00Z", "2026-09-17T18:00:01Z", error);

    // ---- confirm stage ---------------------------------------------------------------------------

    [Fact]
    public void BeginRenameConfirm_moves_to_the_rename_screen_without_calling_the_link()
    {
        var (c, link, _) = Build();

        c.BeginRenameConfirm("/lib/photos");

        Assert.Equal(AppScreen.Rename, c.Screen);
        Assert.Equal(RenameStage.Confirming, c.Rename.Stage);
        Assert.Equal("/lib/photos", c.Rename.Folder);
        Assert.Contains("/lib/photos", c.Rename.ConfirmText);
        Assert.Empty(link.Calls);
    }

    [Fact]
    public void CancelRenameConfirm_returns_to_the_start_screen_and_calls_nothing()
    {
        var (c, link, _) = Build();
        c.BeginRenameConfirm("/lib/photos");

        c.CancelRenameConfirm();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task ConfirmRenameAsync_on_the_already_open_folder_does_not_reopen_it()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "preparing", 0, 6)));

        await c.ConfirmRenameAsync();

        Assert.Equal(0, link.CallCount(nameof(link.OpenAsync)));
        Assert.Equal(1, link.CallCount(nameof(link.StartRenameAsync)));
        Assert.Equal(AppScreen.Rename, c.Screen);
        Assert.Equal(RenameStage.Running, c.Rename.Stage);
    }

    [Fact]
    public async Task ConfirmRenameAsync_on_a_different_folder_opens_it_first()
    {
        var current = SnapshotBuilder.Ranking(folder: "/lib/other");
        var target = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(current);
        c.BeginRenameConfirm("/lib/photos");
        link.OpenResults.Enqueue(new OpenResult.Opened(target, false));
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "preparing", 0, 6)));

        await c.ConfirmRenameAsync();

        Assert.Equal(1, link.CallCount(nameof(link.OpenAsync)));
        Assert.Equal(1, link.CallCount(nameof(link.StartRenameAsync)));
        Assert.Equal(RenameStage.Running, c.Rename.Stage);
    }

    [Fact]
    public async Task ConfirmRenameAsync_when_the_folder_cannot_be_opened_returns_to_start_with_the_refusal()
    {
        var current = SnapshotBuilder.Ranking(folder: "/lib/other");
        var (c, link, _) = Build(current);
        c.BeginRenameConfirm("/lib/photos");
        var failure = new Failure(FailureKind.FolderNotRankable, "Nothing to rank here", "/lib/photos has 1 picture.", "folder_not_rankable", null, Fatal: false);
        link.OpenResults.Enqueue(new OpenResult.Failed(failure));

        await c.ConfirmRenameAsync();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("Nothing to rank here", c.Start.BoxText);
        Assert.Equal(0, link.CallCount(nameof(link.StartRenameAsync)));
    }

    [Fact]
    public async Task ConfirmRenameAsync_refused_rename_in_progress_returns_to_start_with_the_refusal()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        var failure = new Failure(FailureKind.Unexpected, "A rename is already running", "Operation op-9 is already in progress.", "rename_in_progress", null, Fatal: false);
        link.StartRenameResults.Enqueue(new RenameOperationResult.Refused(failure));

        await c.ConfirmRenameAsync();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("already running", c.Start.BoxText);
    }

    [Fact]
    public async Task ConfirmRenameAsync_whose_run_is_already_terminal_in_the_202_returns_to_start_at_once()
    {
        // Six local files: "over before a human can react" (SERVER_SPEC.md § 10.16) — the 202 body
        // itself can already be succeeded.
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("succeeded", "done", 6, 6)));

        await c.ConfirmRenameAsync();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("Renamed 6 file", c.Start.BoxText);
        Assert.Equal(1, link.CallCount(nameof(link.CloseAsync)));
    }

    // ---- polling (Tick) ----------------------------------------------------------------------------

    [Fact]
    public async Task Tick_polls_GetRename_while_running_and_stops_polling_once_terminal()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 0, 6)));
        await c.ConfirmRenameAsync();

        link.GetRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 3, 6)));
        c.Tick();
        await Flush();
        Assert.Equal(3, c.Rename.Done);
        Assert.Equal(AppScreen.Rename, c.Screen);

        link.GetRenameResults.Enqueue(new RenameOperationResult.Observed(Op("succeeded", "done", 6, 6)));
        c.Tick();
        await Flush();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("Renamed 6 file", c.Start.BoxText);

        // No further polling once the screen has already moved on.
        c.Tick();
        await Flush();
        Assert.Equal(2, link.CallCount(nameof(link.GetRenameAsync))); // both enqueued results consumed, no third call attempted
    }

    [Fact]
    public async Task Tick_does_not_overlap_a_poll_already_in_flight()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 0, 6)));
        await c.ConfirmRenameAsync();

        Assert.False(c.Rename.Busy);
        // Nothing is scripted for GetRenameAsync -- if Tick ignored the busy guard it would throw
        // ("no scripted result") the moment it tried to call it.
        c.Rename.EnterBusy(); // simulate a poll already in flight
        c.Tick();
        Assert.Equal(0, link.CallCount(nameof(link.GetRenameAsync)));
    }

    private static async Task Flush() => await Task.Yield();

    // ---- cancel --------------------------------------------------------------------------------

    [Fact]
    public async Task RequestCancelRename_while_idle_fires_the_cancel_call_at_once()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 1, 6)));
        await c.ConfirmRenameAsync();

        link.CancelRenameResults.Enqueue(new RenameOperationResult.Observed(Op("cancelled", "done", 3, 6)));
        c.RequestCancelRename();
        await Flush();

        Assert.Equal(1, link.CallCount(nameof(link.CancelRenameAsync)));
        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("cancelled", c.Start.BoxText);
    }

    [Fact]
    public async Task RequestCancelRename_twice_only_marks_the_request_once_before_it_lands()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 1, 6)));
        await c.ConfirmRenameAsync();

        c.Rename.EnterBusy(); // simulate a poll already in flight
        c.RequestCancelRename();
        c.RequestCancelRename();

        Assert.True(c.Rename.CancelRequested);
        // Nothing sent yet -- still busy from the simulated in-flight poll; the pending cancel rides
        // that poll's own completion (PollRenameOnceAsync), not a second, overlapping call here.
        Assert.Equal(0, link.CallCount(nameof(link.CancelRenameAsync)));
    }

    [Fact]
    public void OnRenameEscape_while_confirming_cancels_the_confirmation_not_the_run()
    {
        var (c, link, _) = Build();
        c.BeginRenameConfirm("/lib/photos");

        c.OnRenameEscape();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task OnRenameEscape_while_running_cancels_the_run()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 1, 6)));
        await c.ConfirmRenameAsync();

        link.CancelRenameResults.Enqueue(new RenameOperationResult.Observed(Op("cancelled", "done", 2, 6)));
        c.OnRenameEscape();
        await Flush();

        Assert.Equal(1, link.CallCount(nameof(link.CancelRenameAsync)));
        Assert.Equal(AppScreen.Start, c.Screen);
    }

    // ---- every terminus (§ 3.13 item 2) --------------------------------------------------------

    [Theory]
    [InlineData("succeeded", "Renamed")]
    [InlineData("cancelled", "cancelled")]
    public async Task Terminal_states_show_one_line_and_return_to_start(string state, string expectedSubstring)
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 0, 6)));
        await c.ConfirmRenameAsync();

        link.GetRenameResults.Enqueue(new RenameOperationResult.Observed(Op(state, "done", 6, 6)));
        c.Tick();
        await Flush();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains(expectedSubstring, c.Start.BoxText);
    }

    [Fact]
    public async Task Failed_with_reunited_true_says_nothing_was_touched()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 0, 6)));
        await c.ConfirmRenameAsync();

        link.GetRenameResults.Enqueue(new RenameOperationResult.Observed(
            Op("failed", "done", 2, 6, new RenameOperationError("rename_failed", Reunited: true, Journal: null))));
        c.Tick();
        await Flush();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("nothing was renamed", c.Start.BoxText);
    }

    [Fact]
    public async Task Failed_with_reunited_false_shows_the_journal_path_and_says_to_retry()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 0, 6)));
        await c.ConfirmRenameAsync();

        link.GetRenameResults.Enqueue(new RenameOperationResult.Observed(
            Op("failed", "done", 2, 6, new RenameOperationError("rename_failed", Reunited: false, Journal: "/lib/photos/.rankmaster-rename.json"))));
        c.Tick();
        await Flush();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("/lib/photos/.rankmaster-rename.json", c.Start.BoxText);
        Assert.Contains("retry", c.Start.BoxText);
    }

    // ---- transient poll failures do not abandon the bar (§ 3.5's "say nothing on a hiccup") ------

    [Fact]
    public async Task A_transient_refusal_while_polling_keeps_the_rename_screen_up()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 1, 6)));
        await c.ConfirmRenameAsync();

        var busy = new Failure(FailureKind.ServerBusy, "The server is busy", "Try again in a moment.", "session_busy", null, Fatal: false);
        link.GetRenameResults.Enqueue(new RenameOperationResult.Refused(busy));
        c.Tick();
        await Flush();

        Assert.Equal(AppScreen.Rename, c.Screen);
        Assert.Equal(RenameStage.Running, c.Rename.Stage);
    }

    [Fact]
    public async Task A_fatal_refusal_while_polling_returns_to_the_start_screen()
    {
        var snapshot = SnapshotBuilder.Ranking(folder: "/lib/photos");
        var (c, link, _) = Build(snapshot);
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(Op("running", "renaming", 1, 6)));
        await c.ConfirmRenameAsync();

        var lost = new Failure(FailureKind.PairingLost, "This PC is no longer paired", "Restart the server.", "token_revoked", null, Fatal: true);
        link.GetRenameResults.Enqueue(new RenameOperationResult.Refused(lost));
        c.Tick();
        await Flush();

        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.Contains("no longer paired", c.Start.BoxText);
    }
}
