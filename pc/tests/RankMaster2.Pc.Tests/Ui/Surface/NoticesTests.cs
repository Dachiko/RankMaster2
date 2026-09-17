using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 6.1 "Notices": the § 3.4 and § 3.5 tables as data-driven tests, against the real
/// <c>ActionResult</c>/<c>Failure</c> shapes.</summary>
public class NoticesTests
{
    // ---- § 3.4: undo sentences -------------------------------------------------------------------

    [Theory]
    [InlineData("vote", "Vote taken back")]
    [InlineData("skip", "Skip taken back")]
    [InlineData("discard", "Discard taken back")]
    [InlineData("special", "Moved back out of special 1")]
    public void Undo_sentence_by_undoneType(string undoneType, string expected)
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("undo", id: "a.jpg", undoneType: undoneType));
        var notice = Notices.ForResult(RequestedAction.Undo, new ActionResult.Applied(snapshot));
        Assert.Equal(NoticeSurface.Toast, notice.Surface);
        Assert.Equal(expected, notice.Text);
    }

    [Fact]
    public void Undo_sentence_names_the_restored_id_when_it_differs()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("undo", id: "a.jpg", restoredId: "a (2).jpg", undoneType: "discard"));
        var notice = Notices.ForResult(RequestedAction.Undo, new ActionResult.Applied(snapshot));
        Assert.Equal("Discard taken back as a (2).jpg", notice.Text);
    }

    [Fact]
    public void Undo_sentence_omits_the_suffix_when_restoredId_equals_id()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("undo", id: "a.jpg", restoredId: "a.jpg", undoneType: "special"));
        var notice = Notices.ForResult(RequestedAction.Undo, new ActionResult.Applied(snapshot));
        Assert.Equal("Moved back out of special 1", notice.Text);
    }

    [Fact]
    public void No_lastAction_undoneType_is_silent()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: null);
        var notice = Notices.ForResult(RequestedAction.Undo, new ActionResult.Applied(snapshot));
        Assert.Equal(Notice.Silent, notice);
    }

    // ---- file actions vs vote/skip (plan § 1.2) ------------------------------------------------

    [Fact]
    public void Vote_applied_is_silent()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("vote"));
        Assert.Equal(Notice.Silent, Notices.ForResult(RequestedAction.Vote, new ActionResult.Applied(snapshot)));
    }

    [Fact]
    public void Skip_applied_is_silent()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("skip"));
        Assert.Equal(Notice.Silent, Notices.ForResult(RequestedAction.Skip, new ActionResult.Applied(snapshot)));
    }

    [Fact]
    public void Discard_applied_toasts_the_filename()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("discard", id: "a.jpg"));
        var notice = Notices.ForResult(RequestedAction.Discard, new ActionResult.Applied(snapshot));
        Assert.Equal(Notice.Toast("Discarded a.jpg"), notice);
    }

    [Fact]
    public void Special_applied_toasts_special_one()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("special", id: "a.jpg"));
        var notice = Notices.ForResult(RequestedAction.Special, new ActionResult.Applied(snapshot));
        Assert.Equal(Notice.Toast("Moved a.jpg to special 1"), notice);
    }

    [Fact]
    public void Drop_missing_toasts_removed_wording()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("drop_missing", id: "gone.jpg"));
        var notice = Notices.ForResult(RequestedAction.Discard, new ActionResult.Applied(snapshot));
        Assert.Equal(Notice.Toast("Removed gone.jpg from the ranking"), notice);
    }

    [Fact]
    public void Save_applied_says_saved()
    {
        var snapshot = SnapshotBuilder.Ranking();
        Assert.Equal(Notice.Toast("Saved"), Notices.ForResult(RequestedAction.Save, new ActionResult.Applied(snapshot)));
    }

    // ---- resynchronised -----------------------------------------------------------------------

    [Fact]
    public void LandedEarlier_is_treated_as_applied()
    {
        var snapshot = SnapshotBuilder.Ranking(lastAction: SnapshotBuilder.Action("discard", id: "a.jpg"));
        var result = new ActionResult.Resynchronised(snapshot, ResyncReason.LandedEarlier);
        Assert.Equal(Notice.Toast("Discarded a.jpg"), Notices.ForResult(RequestedAction.Discard, result));
    }

    [Theory]
    [InlineData(ResyncReason.TokenSpentByOwnEarlierRequest)]
    [InlineData(ResyncReason.PairMovedElsewhere)]
    [InlineData(ResyncReason.OutcomeUnknownAfterRestart)]
    [InlineData(ResyncReason.SessionReplaced)]
    [InlineData(ResyncReason.UndoAlreadyDone)]
    [InlineData(ResyncReason.NoCurrentPair)]
    public void Every_other_resync_reason_is_silent(ResyncReason why)
    {
        var snapshot = SnapshotBuilder.Ranking();
        var result = new ActionResult.Resynchronised(snapshot, why);
        Assert.Equal(Notice.Silent, Notices.ForResult(RequestedAction.Vote, result));
    }

    [Fact]
    public void NotSent_is_always_silent()
    {
        foreach (var reason in Enum.GetValues<NotSentReason>())
            Assert.Equal(Notice.Silent, Notices.ForResult(RequestedAction.Undo, new ActionResult.NotSent(reason)));
    }

    // ---- failures: B already wrote the words; E only picks the surface -----------------------------

    [Fact]
    public void Non_fatal_failure_is_a_toast_built_from_title_and_detail()
    {
        var failure = new Failure(FailureKind.MoveFailed, "The file could not be moved", "It is probably open elsewhere.", "move_failed", "req-1", Fatal: false);
        var notice = Notices.ForResult(RequestedAction.Discard, new ActionResult.Refused(failure, null));
        Assert.Equal(NoticeSurface.Toast, notice.Surface);
        Assert.Contains("The file could not be moved", notice.Text);
        Assert.Contains("It is probably open elsewhere.", notice.Text);
    }

    [Fact]
    public void Fatal_failure_goes_to_the_start_screen()
    {
        var failure = new Failure(FailureKind.PairingLost, "This PC is no longer paired", "Restart the server.", "token_revoked", null, Fatal: true);
        var notice = Notices.ForResult(RequestedAction.Vote, new ActionResult.Unknown(failure));
        Assert.Equal(NoticeSurface.StartScreen, notice.Surface);
    }

    [Fact]
    public void Open_failure_always_goes_to_the_start_screen_box_even_when_not_fatal()
    {
        var failure = new Failure(FailureKind.FolderNotRankable, "Nothing to rank here", "/lib has 1 picture.", "folder_not_rankable", null, Fatal: false);
        var notice = Notices.ForOpenFailure(failure);
        Assert.Equal(NoticeSurface.StartScreen, notice.Surface);
        Assert.Contains("Nothing to rank here", notice.Text);
    }
}
