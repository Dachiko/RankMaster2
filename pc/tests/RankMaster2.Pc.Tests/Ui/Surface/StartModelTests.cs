using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 6.1 "StartModel/LastFolderStore".</summary>
public class StartModelTests
{
    [Fact]
    public void Opening_sets_the_folder_and_clears_any_message()
    {
        var m = new StartModel();
        m.ShowMessage("something wrong");
        m.BeginOpening("/lib");
        Assert.True(m.Opening);
        Assert.Equal("/lib", m.OpeningFolder);
        Assert.Equal(StartMessageKind.None, m.MessageKind);
        Assert.Null(m.MessageText);
    }

    [Fact]
    public void A_failed_open_ends_opening_and_shows_its_sentence_as_an_error()
    {
        var m = new StartModel();
        m.BeginOpening("/lib");
        m.ShowMessage("Nothing to rank here · /lib has 1 picture");
        Assert.False(m.Opening);
        Assert.Null(m.OpeningFolder);
        Assert.Equal(StartMessageKind.Error, m.MessageKind);
        Assert.Equal("Nothing to rank here · /lib has 1 picture", m.MessageText);
    }

    [Fact]
    public void A_message_that_is_not_an_error_is_Done()
    {
        var m = new StartModel();
        m.ShowMessage("Renamed 6 files · photos", isError: false);
        Assert.Equal(StartMessageKind.Done, m.MessageKind);
    }

    [Fact]
    public void Exhausted_is_its_own_kind_names_the_folder_and_offers_undo_only_when_available()
    {
        var m = new StartModel();
        m.ShowExhausted("Holiday 2024", undoAvailable: true);
        Assert.Equal(StartMessageKind.Exhausted, m.MessageKind);
        Assert.Contains("Holiday 2024", m.MessageText);
        Assert.True(m.UndoAvailable);
        Assert.True(m.ExhaustedSessionOpen);

        m.ShowExhausted("Holiday 2024", undoAvailable: false);
        Assert.Equal(StartMessageKind.Exhausted, m.MessageKind);
        Assert.False(m.UndoAvailable);
    }

    [Fact]
    public void A_message_after_exhausted_replaces_the_kind_and_drops_the_undo()
    {
        var m = new StartModel();
        m.ShowExhausted("x", true);
        m.ShowMessage("Rename failed · nothing changed");
        Assert.Equal(StartMessageKind.Error, m.MessageKind);
        Assert.False(m.UndoAvailable);
        Assert.False(m.ExhaustedSessionOpen);
    }

    [Fact]
    public void ClearBox_clears_the_message_and_the_exhausted_flag()
    {
        var m = new StartModel();
        m.ShowExhausted("x", true);
        m.ClearBox();
        Assert.Equal(StartMessageKind.None, m.MessageKind);
        Assert.Null(m.MessageText);
        Assert.False(m.UndoAvailable);
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
    public void A_server_that_is_not_there_reads_Server_not_answering_plus_the_detail()
    {
        var failure = new Failure(FailureKind.ServerNotRunning, "The Rank Master server is not running",
            "Start RankMaster2.Tray.exe yourself, then try again.", "client_unreachable", null, Fatal: false);
        var line = StartModel.StatusLineFor(LinkState.Disconnected, failure);
        Assert.Equal("Server not answering · Start RankMaster2.Tray.exe yourself, then try again", line);
    }

    [Fact]
    public void A_server_that_is_not_there_and_has_no_detail_is_just_the_short_line()
    {
        var failure = new Failure(FailureKind.Unreachable, "The server did not answer", "", "client_unreachable", null, Fatal: false);
        Assert.Equal("Server not answering", StartModel.StatusLineFor(LinkState.InSessionUnreachable, failure));
    }

    [Fact]
    public void Any_other_failure_keeps_its_title_joined_to_the_detail_without_a_full_stop()
    {
        var failure = new Failure(FailureKind.PairingLost, "This PC is no longer paired", "Restart the server.", "token_revoked", null, Fatal: true);
        Assert.Equal("This PC is no longer paired · Restart the server", StartModel.StatusLineFor(LinkState.Disconnected, failure));
    }

    [Fact]
    public void Disconnected_with_no_failure_yet_has_a_plain_default()
    {
        Assert.Equal("Connecting to the server", StartModel.StatusLineFor(LinkState.Disconnected, null));
        Assert.Equal("Server not answering", StartModel.StatusLineFor(LinkState.InSessionUnreachable, null));
    }

    // ---- corner lamp (plan H § 3.1) ---------------------------------------------------------------

    [Fact]
    public void The_lamp_is_ink_when_connected_faint_before_the_first_answer_and_red_when_down()
    {
        var failure = new Failure(FailureKind.ServerNotRunning, "x", "", "client_unreachable", null, Fatal: false);
        Assert.Equal(ServerLamp.Ok, StartModel.LampFor(LinkState.Connected, null));
        Assert.Equal(ServerLamp.Ok, StartModel.LampFor(LinkState.InSession, null));
        Assert.Equal(ServerLamp.Waiting, StartModel.LampFor(LinkState.Disconnected, null));
        Assert.Equal(ServerLamp.Down, StartModel.LampFor(LinkState.Disconnected, failure));
        Assert.Equal(ServerLamp.Down, StartModel.LampFor(LinkState.InSessionUnreachable, null));
    }
}
