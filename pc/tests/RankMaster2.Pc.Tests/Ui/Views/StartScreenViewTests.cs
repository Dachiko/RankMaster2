using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// Plan H § 3.1-§ 3.4, headless: the start screen's keys (R raises the rename request; F1 opens and
/// closes the keys page; Esc closes the page instead of quitting; ← / → move the pill focus), what the
/// start screen shows in each state, and that the rename card is drawn over a start screen that stays in
/// the tree. No window is shown on screen (Avalonia's headless platform) and nothing is drawn.
/// </summary>
public class StartScreenViewTests
{
    private static (Window Window, UiRoot Root, RankCoordinator Coordinator, FakeSessionLink Link) Build(string? lastFolder = null)
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking() };
        var store = new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-house-view-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt"));
        if (lastFolder is not null) store.Save(lastFolder);
        var coordinator = new RankCoordinator(link, new FakeStillSource(), null, new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1)),
            new SynchronousUiThread(), new ImmediateDelay(), store);
        var root = new UiRoot(coordinator);
        var window = new Window { Content = root, Width = 1280, Height = 720 };
        window.Show();
        coordinator.InitializeAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        return (window, root, coordinator, link);
    }

    private static string FolderThatExists(string leaf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-house-view-folders-" + Guid.NewGuid().ToString("N"), leaf);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- keys -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void R_on_the_start_screen_raises_the_rename_request()
    {
        var (window, _, c, _) = Build();
        var raised = 0;
        c.RenameRequested += () => raised++;

        window.KeyPress(Key.R, RawInputModifiers.None);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void R_while_a_folder_is_opening_does_nothing()
    {
        var (window, _, c, _) = Build();
        c.Start.BeginOpening("/lib");
        var raised = 0;
        c.RenameRequested += () => raised++;

        window.KeyPress(Key.R, RawInputModifiers.None);

        Assert.Equal(0, raised);
    }

    [AvaloniaFact]
    public void O_still_raises_the_open_request()
    {
        var (window, _, c, _) = Build();
        var raised = 0;
        c.OpenFolderRequested += () => raised++;

        window.KeyPress(Key.O, RawInputModifiers.None);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void F1_opens_the_keys_page_and_F1_closes_it()
    {
        var (window, root, c, _) = Build();
        var page = Find<KeysPage>(root)!;
        Assert.False(page.IsOpen);

        window.KeyPress(Key.F1, RawInputModifiers.None);
        Assert.True(c.Rank.HelpPinned);
        Assert.True(page.IsOpen);

        window.KeyPress(Key.F1, RawInputModifiers.None);
        Assert.False(c.Rank.HelpPinned);
        Assert.False(page.IsOpen);
    }

    [AvaloniaFact]
    public void Esc_with_the_keys_page_open_closes_it_and_does_not_quit_but_Esc_alone_quits()
    {
        var (window, root, c, _) = Build();
        var quit = 0;
        c.QuitRequested += () => quit++;

        window.KeyPress(Key.F1, RawInputModifiers.None);
        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Assert.Equal(0, quit);
        Assert.False(Find<KeysPage>(root)!.IsOpen);

        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Assert.Equal(1, quit);
    }

    [AvaloniaFact]
    public void Any_key_closes_the_keys_page_and_does_nothing_else()
    {
        var (window, root, c, _) = Build();
        var opened = 0;
        c.OpenFolderRequested += () => opened++;

        window.KeyPress(Key.F1, RawInputModifiers.None);
        window.KeyPress(Key.O, RawInputModifiers.None); // closes the page; does not also open the picker

        Assert.False(Find<KeysPage>(root)!.IsOpen);
        Assert.Equal(0, opened);
    }

    [AvaloniaFact]
    public void The_keys_page_lists_the_three_columns_and_the_version()
    {
        var (window, root, _, _) = Build();
        window.KeyPress(Key.F1, RawInputModifiers.None); // a page that is not shown has not built its rows
        var page = Find<KeysPage>(root)!;
        var texts = page.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("RANK", texts);
        Assert.Contains("START", texts);
        Assert.Contains("EVERYWHERE", texts);
        Assert.Contains("Rename by rank", texts);
        Assert.Contains("ANY KEY CLOSES", texts);
        Assert.Contains(texts, t => t is not null && t.StartsWith('v') && t.Length > 1 && char.IsDigit(t[1]));
        // Every key chip of every row is on the page: R, O, F1 and Esc among them.
        var chips = page.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("kbd"))
            .Select(b => ((TextBlock)b.Child!).Text).ToList();
        Assert.Contains("R", chips);
        Assert.Contains("F1", chips);
        Assert.Contains("Esc", chips);
    }

    [AvaloniaFact]
    public void Arrow_keys_move_focus_between_the_pills_without_wrapping()
    {
        var folder = FolderThatExists("Iceland 2026");
        var (window, root, _, _) = Build(folder);
        var start = Find<StartView>(root)!;
        Assert.Equal("ResumeButton", start.FocusedPill); // Resume is focused when shown

        window.KeyPress(Key.Right, RawInputModifiers.None);
        Assert.Equal("OpenButton", start.FocusedPill);
        window.KeyPress(Key.Right, RawInputModifiers.None);
        Assert.Equal("RenameButton", start.FocusedPill);
        window.KeyPress(Key.Right, RawInputModifiers.None);
        Assert.Equal("RenameButton", start.FocusedPill);
        window.KeyPress(Key.Left, RawInputModifiers.None);
        Assert.Equal("OpenButton", start.FocusedPill);
    }

    [AvaloniaFact]
    public void With_no_last_folder_Open_is_the_focused_pill_and_Resume_is_not_shown()
    {
        var (_, root, _, _) = Build();
        var start = Find<StartView>(root)!;
        Assert.Equal("OpenButton", start.FocusedPill);
        Assert.False(Find<Button>(start, "ResumeButton")!.IsVisible);
    }

    // ---- what it shows ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void The_hero_is_the_last_folders_name_in_upper_case_and_the_path_sits_under_it()
    {
        var folder = FolderThatExists("Iceland 2026");
        var (_, root, _, _) = Build(folder);
        var start = Find<StartView>(root)!;

        Assert.Equal("ICELAND 2026", Find<TextBlock>(start, "HeroText")!.Text);
        Assert.Equal(folder, Find<TextBlock>(start, "PathText")!.Text);
        Assert.Equal("RESUME", Find<TextBlock>(start, "LabelText")!.Text);
    }

    [AvaloniaFact]
    public void With_no_last_folder_the_hero_is_RANK_MASTER_and_no_path_row_shows()
    {
        var (_, root, _, _) = Build();
        var start = Find<StartView>(root)!;

        Assert.Equal("RANK MASTER", Find<TextBlock>(start, "HeroText")!.Text);
        Assert.False(Find<Grid>(start, "UnderRow")!.IsVisible);
    }

    [AvaloniaFact]
    public void Opening_dims_the_pills_and_shows_the_busy_line_instead_of_the_path()
    {
        var folder = FolderThatExists("Iceland 2026");
        var (_, root, c, _) = Build(folder);
        var start = Find<StartView>(root)!;

        c.Start.BeginOpening(folder);
        start.Refresh();

        Assert.Equal(0.35, Find<Button>(start, "OpenButton")!.Opacity, 2);
        Assert.False(Find<Button>(start, "OpenButton")!.IsEnabled);
        Assert.True(Find<BusyLine>(start, "Busy")!.IsVisible);
        Assert.Equal(0, Find<TextBlock>(start, "PathText")!.Opacity);
    }

    [AvaloniaFact]
    public void Exhausted_relabels_the_hero_and_turns_the_first_pill_into_take_back()
    {
        var folder = FolderThatExists("Iceland 2026");
        var (_, root, c, _) = Build(folder);
        var start = Find<StartView>(root)!;

        c.Start.ShowExhausted("Iceland 2026", undoAvailable: true);
        start.Refresh();

        Assert.Equal("NO PAIR LEFT", Find<TextBlock>(start, "LabelText")!.Text);
        Assert.True(Find<Avalonia.Controls.Shapes.Ellipse>(start, "LabelDot")!.IsVisible);
        Assert.Equal("CTRL+Z TAKE BACK", Find<TextBlock>(start, "ResumeText")!.Text);
        Assert.Equal("ResumeButton", start.FocusedPill);
    }

    [AvaloniaFact]
    public void An_error_message_is_the_status_line_in_accent_and_a_finished_rename_is_mid_with_a_dot()
    {
        var (_, root, c, _) = Build();
        var start = Find<StartView>(root)!;
        var status = Find<TextBlock>(start, "StatusLine")!;
        var dot = Find<Avalonia.Controls.Shapes.Ellipse>(start, "StatusDot")!;

        c.Start.ShowMessage(@"Nothing to rank here · D:\Downloads has no photos or videos");
        start.Refresh();
        Assert.Equal(@"Nothing to rank here · D:\Downloads has no photos or videos", status.Text);
        Assert.True(status.Classes.Contains("accent"));
        Assert.False(dot.IsVisible);

        c.Start.ShowMessage("Renamed 6 files · photos", isError: false);
        start.Refresh();
        Assert.Equal("Renamed 6 files · photos", status.Text);
        Assert.True(status.Classes.Contains("mid"));
        Assert.False(status.Classes.Contains("accent"));
        Assert.True(dot.IsVisible);
    }

    [AvaloniaFact]
    public void No_server_turns_the_corner_lamp_and_the_status_line_red_and_nothing_else()
    {
        var (_, root, c, link) = Build();
        var start = Find<StartView>(root)!;
        link.State = LinkState.Disconnected;
        link.LastFailure = new Failure(FailureKind.ServerNotRunning, "The Rank Master server is not running", "", "client_unreachable", null, Fatal: false);
        start.Refresh();

        Assert.Equal("NO SERVER", Find<TextBlock>(start, "ServerText")!.Text);
        Assert.True(Find<TextBlock>(start, "ServerText")!.Classes.Contains("accent"));
        Assert.True(Find<Avalonia.Controls.Shapes.Ellipse>(start, "ServerDot")!.Classes.Contains("live"));
        var status = Find<TextBlock>(start, "StatusLine")!;
        Assert.Equal("Server not answering", status.Text);
        Assert.True(status.Classes.Contains("accent"));
        Assert.Equal("RANK MASTER", Find<TextBlock>(start, "HeroText")!.Text);
    }

    [AvaloniaFact]
    public void A_connected_server_shows_the_ink_lamp()
    {
        var (_, root, _, _) = Build();
        var start = Find<StartView>(root)!;
        Assert.Equal("SERVER", Find<TextBlock>(start, "ServerText")!.Text);
        Assert.False(Find<Avalonia.Controls.Shapes.Ellipse>(start, "ServerDot")!.Classes.Contains("live"));
    }

    // ---- rename card over the start screen ----------------------------------------------------

    [AvaloniaFact]
    public void The_start_screen_stays_in_the_tree_under_the_rename_card_and_goes_inert()
    {
        var folder = FolderThatExists("Iceland 2026");
        var (_, root, c, _) = Build(folder);
        var start = Find<StartView>(root)!;
        var card = Find<RenameView>(root)!;
        Assert.False(card.IsShown);

        c.BeginRenameConfirm(folder);
        Dispatcher.UIThread.RunJobs();

        Assert.True(card.IsShown);
        Assert.NotNull(start.GetVisualRoot());
        Assert.False(start.IsEnabled);
        var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Contains("RENAME BY RANK", texts);
        Assert.Contains("ICELAND 2026", texts);
        Assert.Contains(RenameModel.ConfirmLine, texts);
        Assert.Contains("ENTER RENAME", texts);
        Assert.Contains("ESC KEEP", texts);

        c.CancelRenameConfirm();
        Dispatcher.UIThread.RunJobs();
        Assert.False(card.IsShown);
        Assert.True(start.IsEnabled);
        Assert.Equal("ResumeButton", start.FocusedPill); // focus is back on the first pill
    }

    [AvaloniaFact]
    public void Enter_on_the_confirming_card_starts_the_rename()
    {
        var (window, _, c, link) = Build();
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(
            new RenameOperation("op-1", "running", "renaming", 0, 6, "2026-10-04T10:00:00Z", "2026-10-04T10:00:00Z", null)));

        window.KeyPress(Key.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, link.CallCount(nameof(link.StartRenameAsync)));
        Assert.Equal(RenameStage.Running, c.Rename.Stage);
    }

    [AvaloniaFact]
    public void The_running_card_shows_the_percent_and_never_done_over_total()
    {
        var (_, root, c, link) = Build();
        var card = Find<RenameView>(root)!;
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(
            new RenameOperation("op-1", "running", "renaming", 22, 60, "2026-10-04T10:00:00Z", "2026-10-04T10:00:00Z", null)));
        c.ConfirmRenameAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("36", Find<TextBlock>(card, "PercentText")!.Text);
        Assert.Equal(DotRow.DotsFor(36, 60), (int)Find<DotRow>(card, "Dots")!.Filled);
        var all = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? "").ToList();
        Assert.DoesNotContain(all, t => t.Contains("22") || t.Contains("/ 60"));
        Assert.Contains("ESC CANCEL", all);
        // The current phase word (renaming = RENAME) is the accent one.
        Assert.True(Find<TextBlock>(card, "PhaseRename")!.Classes.Contains("accent"));
        Assert.False(Find<TextBlock>(card, "PhasePrepare")!.Classes.Contains("accent"));
    }

    [AvaloniaFact]
    public void Cancel_requested_says_CANCELLING_and_a_busy_line_replaces_the_dots()
    {
        var (_, root, c, link) = Build();
        var card = Find<RenameView>(root)!;
        c.BeginRenameConfirm("/lib/photos");
        link.StartRenameResults.Enqueue(new RenameOperationResult.Observed(
            new RenameOperation("op-1", "running", "renaming", 1, 6, "2026-10-04T10:00:00Z", "2026-10-04T10:00:00Z", null)));
        c.ConfirmRenameAsync().GetAwaiter().GetResult();

        c.RequestCancelRename();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("CANCELLING", Find<TextBlock>(card, "CancelCaption")!.Text);
        Assert.False(Find<DotRow>(card, "Dots")!.IsVisible);
        Assert.True(Find<BusyLine>(card, "CancellingLine")!.IsVisible);
    }

    // ---- font resource -------------------------------------------------------------------------

    [AvaloniaFact]
    public void The_Doto_font_is_an_embedded_resource_the_house_font_family_points_at()
    {
        Assert.True(AssetLoader.Exists(new Uri("avares://RankMaster2/Ui/Fonts/Doto_Rounded-Black.ttf")));

        var (_, root, _, _) = Build();
        Assert.True(root.TryFindResource("House.Dot", out var family));
        Assert.IsType<Avalonia.Media.FontFamily>(family);
        Assert.Contains("avares://RankMaster2/Ui/Fonts#Doto Rounded", family!.ToString());
    }

    private static T? Find<T>(Control root, string? name = null) where T : Control
    {
        if (root is T match && (name is null || root.Name == name)) return match;
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control c && Find<T>(c, name) is { } found) return found;
        }
        return null;
    }
}
