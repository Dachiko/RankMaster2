using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 6.1 "StartModel/LastFolderStore".</summary>
public class StartModelTests
{
    [Fact]
    public void Opening_sets_the_folder_and_clears_any_box()
    {
        var m = new StartModel();
        m.ShowMessage("something wrong");
        m.BeginOpening("/lib");
        Assert.True(m.Opening);
        Assert.Equal("/lib", m.OpeningFolder);
        Assert.Null(m.BoxText);
    }

    [Fact]
    public void A_failed_open_ends_opening_and_shows_its_sentence()
    {
        var m = new StartModel();
        m.BeginOpening("/lib");
        m.ShowMessage("Nothing to rank here — /lib has 1 picture.");
        Assert.False(m.Opening);
        Assert.Null(m.OpeningFolder);
        Assert.Equal("Nothing to rank here — /lib has 1 picture.", m.BoxText);
    }

    [Fact]
    public void Exhausted_names_the_folder_and_offers_undo_only_when_available()
    {
        var m = new StartModel();
        m.ShowExhausted("Holiday 2024", undoAvailable: true);
        Assert.Contains("Holiday 2024", m.BoxText);
        Assert.Contains("Ctrl+Z", m.BoxText);
        Assert.True(m.ExhaustedSessionOpen);

        m.ShowExhausted("Holiday 2024", undoAvailable: false);
        Assert.DoesNotContain("Ctrl+Z", m.BoxText);
    }

    [Fact]
    public void ClearBox_also_clears_the_exhausted_flag()
    {
        var m = new StartModel();
        m.ShowExhausted("x", true);
        m.ClearBox();
        Assert.Null(m.BoxText);
        Assert.False(m.ExhaustedSessionOpen);
    }

    // ---- status line -----------------------------------------------------------------------------

    [Fact]
    public void Connected_or_InSession_shows_nothing()
    {
        Assert.Null(StartModel.StatusLineFor(LinkState.Connected, null));
        Assert.Null(StartModel.StatusLineFor(LinkState.InSession, null));
    }

    [Fact]
    public void A_failure_is_shown_in_its_own_words()
    {
        var failure = new Failure(FailureKind.ServerNotRunning, "The Rank Master server is not running",
            "Start RankMaster2.Tray.exe yourself, then try again.", "client_unreachable", null, Fatal: false);
        var line = StartModel.StatusLineFor(LinkState.Disconnected, failure);
        Assert.Contains("The Rank Master server is not running", line);
        Assert.Contains("Start RankMaster2.Tray.exe", line);
    }

    [Fact]
    public void Disconnected_with_no_failure_yet_has_a_plain_default()
    {
        Assert.NotNull(StartModel.StatusLineFor(LinkState.Disconnected, null));
    }
}
