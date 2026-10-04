using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>
/// Plan H § 3.1/§ 3.2: every sentence the start screen and the rename card say, shortened to
/// "what happened · what it means" with no trailing full stop; the rename card's numbers
/// (percent, phase word, folder name) as pure functions of <see cref="RenameModel"/>.
/// </summary>
public class HouseSentenceTests
{
    private static RenameOperation Op(string state, string phase = "done", int done = 0, int total = 0, RenameOperationError? error = null) =>
        new("op-1", state, phase, done, total, "2026-10-04T10:00:00Z", "2026-10-04T10:00:01Z", error);

    // ---- Notices.ForRenameTerminal (plan H § 3.2's four new sentences) ----------------------------

    [Fact]
    public void Succeeded_names_the_count_and_the_folders_own_name()
    {
        Assert.Equal("Renamed 6 files · photos", Notices.ForRenameTerminal(Op("succeeded", done: 6, total: 6), "/lib/photos"));
        Assert.Equal("Renamed 6 files · Iceland 2026", Notices.ForRenameTerminal(Op("succeeded", done: 6, total: 6), @"D:\Photos\Iceland 2026\"));
    }

    [Fact]
    public void Succeeded_with_one_file_is_singular_and_a_big_count_is_grouped_without_a_breaking_space()
    {
        Assert.Equal("Renamed 1 file · x", Notices.ForRenameTerminal(Op("succeeded", total: 1), "/x"));
        // U+00A0 between the groups: the line must not wrap inside a number.
        Assert.Equal("Renamed 1\u00A0284 files · x", Notices.ForRenameTerminal(Op("succeeded", total: 1284), "/x"));
    }

    [Fact]
    public void Cancelled_says_what_stays()
    {
        Assert.Equal("Rename stopped · what was done stays, ratings stay with their files",
            Notices.ForRenameTerminal(Op("cancelled", done: 3, total: 6), "/lib/photos"));
    }

    [Fact]
    public void Failed_and_reunited_says_nothing_changed()
    {
        var error = new RenameOperationError("rename_failed", Reunited: true, Journal: null);
        Assert.Equal("Rename failed · nothing changed", Notices.ForRenameTerminal(Op("failed", error: error), "/lib/photos"));
    }

    [Fact]
    public void Failed_and_not_reunited_says_to_open_the_folder_again_and_gives_the_journal()
    {
        var error = new RenameOperationError("rename_failed", Reunited: false, Journal: "/lib/photos/.rankmaster-rename.json");
        Assert.Equal("Rename failed · open the folder again to retry · /lib/photos/.rankmaster-rename.json",
            Notices.ForRenameTerminal(Op("failed", error: error), "/lib/photos"));
    }

    [Fact]
    public void None_of_the_rename_sentences_ends_in_a_full_stop()
    {
        var sentences = new[]
        {
            Notices.ForRenameTerminal(Op("succeeded", total: 2), "/a"),
            Notices.ForRenameTerminal(Op("cancelled"), "/a"),
            Notices.ForRenameTerminal(Op("failed"), "/a"),
            Notices.ForRenameTerminal(Op("failed", error: new RenameOperationError("x", true, null)), "/a"),
            Notices.ForRenameTerminal(Op("nonsense"), "/a"),
        };
        Assert.All(sentences, s => Assert.False(s.EndsWith('.'), s));
    }

    // ---- start-screen failure text (plan H § 3.1) ----------------------------------------------

    [Fact]
    public void An_open_refusal_is_title_dot_detail_with_no_trailing_full_stop()
    {
        var failure = new Failure(FailureKind.FolderNotRankable, "Nothing to rank here", @"D:\Downloads has no photos or videos.", "folder_not_rankable", null, Fatal: false);
        var notice = Notices.ForOpenFailure(failure);
        Assert.Equal(NoticeSurface.StartScreen, notice.Surface);
        Assert.Equal(@"Nothing to rank here · D:\Downloads has no photos or videos", notice.Text);
    }

    [Fact]
    public void A_fatal_failure_on_the_start_screen_uses_the_same_form_but_a_toast_keeps_its_own()
    {
        var detail = "Restart the server.";
        var fatal = new Failure(FailureKind.PairingLost, "This PC is no longer paired", detail, "token_revoked", null, Fatal: true);
        var soft = new Failure(FailureKind.MoveFailed, "The file could not be moved", "It is probably open elsewhere.", "move_failed", null, Fatal: false);

        Assert.Equal("This PC is no longer paired · Restart the server",
            Notices.ForResult(RequestedAction.Vote, new ActionResult.Unknown(fatal)).Text);
        // The compare screen's toast is not part of plan H: unchanged.
        Assert.Equal("The file could not be moved — It is probably open elsewhere.",
            Notices.ForResult(RequestedAction.Discard, new ActionResult.Unknown(soft)).Text);
    }

    [Theory]
    [InlineData("Title", null, "Title")]
    [InlineData("Title.", "", "Title")]
    [InlineData("Title", "Detail.", "Title · Detail")]
    [InlineData("Title", "  Detail  ", "Title · Detail")]
    public void TitleAndDetail_joins_with_a_middle_dot_and_drops_trailing_stops(string title, string? detail, string expected) =>
        Assert.Equal(expected, Notices.TitleAndDetail(title, detail));

    [Theory]
    [InlineData("/lib/photos", "photos")]
    [InlineData("/lib/photos/", "photos")]
    [InlineData(@"D:\Photos\Iceland 2026", "Iceland 2026")]
    [InlineData(@"D:\", "D:")]
    [InlineData("photos", "photos")]
    public void LeafName_is_the_last_segment_with_either_separator(string folder, string expected) =>
        Assert.Equal(expected, Notices.LeafName(folder));

    // ---- RenameModel's card numbers (plan H § 3.2) ------------------------------------------------

    [Fact]
    public void The_card_names_the_folder_and_says_only_the_pattern_and_that_ratings_stay()
    {
        var m = new RenameModel();
        m.BeginConfirm("/lib/photos");
        Assert.Equal("photos", m.FolderName);
        Assert.Equal("000001-….jpg · ratings stay", RenameModel.ConfirmLine);
    }

    [Theory]
    [InlineData(0, 0, 0)]       // total not known yet (preparing)
    [InlineData(0, 6, 0)]
    [InlineData(3, 6, 50)]
    [InlineData(1, 3, 33)]      // rounded down: never 100 before the run is done
    [InlineData(599, 600, 99)]
    [InlineData(6, 6, 100)]
    [InlineData(9, 6, 100)]     // never over 100
    public void Percent_is_whole_and_rounded_down(int done, int total, int expected)
    {
        var m = new RenameModel();
        m.BeginRunning(Op("running", "renaming", done, total));
        Assert.Equal(expected, m.Percent);
    }

    [Theory]
    [InlineData("preparing", 0)]
    [InlineData("renaming", 1)]
    [InlineData("saving", 2)]
    [InlineData("reuniting", 2)]   // plan H § 3.2: reuniting shows as SAVE
    [InlineData("done", 2)]
    [InlineData("something-new", 0)]
    public void The_phase_word_that_is_current(string phase, int expected)
    {
        var m = new RenameModel();
        m.BeginRunning(Op("running", phase, 1, 6));
        Assert.Equal(expected, m.PhaseStep);
    }
}
