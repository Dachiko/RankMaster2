using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using Xunit;
using static RankMaster2.Pc.Ui.Tests.Surface.BrowseModelTests;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// Plan I § 4 S2, headless (Avalonia's headless platform: no window on screen, nothing drawn): O opens the browser in
/// Rank mode, R in Rename mode, Esc goes back to the screen it came from (the compare screen with the session still
/// open and no Open or Close call made), typed letters reach the filter, F1 opens the browser's keys page.
/// </summary>
public class BrowseScreenViewTests
{
    private const string Images = @"C:\images";
    private const string Garden = @"C:\images\garden";

    private static (Window Window, UiRoot Root, RankCoordinator Coordinator, FakeSessionLink Link) Build(string? lastFolder = Garden, double width = 1280)
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(folder: Garden), ThrowOnOverlappingBrowseCalls = true };
        link.Roots.Add(new LibraryRoot(@"C:\", null, "fixed", true));
        link.Listings[Images] = Listing(Images, @"C:\", Entry(Images, "garden"), Entry(Images, "boxes", rankable: false));
        link.Listings[Garden] = Listing(Garden, Images,
            Entry(Garden, "2025 roses", hasDatabase: true), Entry(Garden, "2024 spring"), Entry(Garden, "Ponds"), Entry(Garden, "Tools", rankable: false));
        var store = new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-browse-view-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt"));
        var coordinator = new RankCoordinator(link, new FakeStillSource(), null, new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1)),
            new SynchronousUiThread(), new ImmediateDelay(), store);
        if (lastFolder is not null) coordinator.Start.LastFolder = lastFolder;
        var root = new UiRoot(coordinator);
        var window = new Window { Content = root, Width = width, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, root, coordinator, link);
    }

    private static T? Find<T>(Control root, string? name = null) where T : Control
    {
        if (root is T match && (name is null || root.Name == name)) return match;
        foreach (var child in root.GetVisualChildren())
            if (child is Control c && Find<T>(c, name) is { } found) return found;
        return null;
    }

    // ---- entering and leaving ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public void O_opens_the_browser_in_Rank_mode_in_the_parent_of_the_last_folder()
    {
        var (window, root, c, _) = Build();

        window.KeyPress(Key.O, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(BrowseMode.Rank, c.Browse.Mode);
        var view = Find<BrowseView>(root)!;
        Assert.NotNull(view);
        Assert.Equal("OPEN", Find<TextBlock>(view, "ModeText")!.Text);
        Assert.False(Find<TextBlock>(view, "ModeText")!.Classes.Contains("accent"));
        Assert.Equal(@"C:\IMAGES", view.PathLineText);
        Assert.Equal("ENTER RANK · → INTO · ← UP · ESC BACK", Find<TextBlock>(view, "KeyLine")!.Text);
        Assert.Null(Find<StartView>(root)); // the browser replaced the start screen
    }

    [AvaloniaFact]
    public void R_opens_the_browser_in_Rename_mode_with_the_word_in_the_accent()
    {
        var (window, root, c, _) = Build();

        window.KeyPress(Key.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(BrowseMode.Rename, c.Browse.Mode);
        var view = Find<BrowseView>(root)!;
        Assert.Equal("RENAME", Find<TextBlock>(view, "ModeText")!.Text);
        Assert.True(Find<TextBlock>(view, "ModeText")!.Classes.Contains("accent"));
        Assert.Equal("ENTER RENAME · → INTO · ← UP · ESC BACK", Find<TextBlock>(view, "KeyLine")!.Text);
    }

    [AvaloniaFact]
    public void Esc_returns_to_the_start_screen_and_does_not_quit()
    {
        var (window, root, c, _) = Build();
        var quit = false;
        c.QuitRequested += () => quit = true;
        window.KeyPress(Key.O, RawInputModifiers.None);

        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(quit);
        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.NotNull(Find<StartView>(root));
        Assert.Null(Find<BrowseView>(root));
    }

    [AvaloniaFact]
    public async Task O_from_the_compare_screen_then_Esc_returns_to_the_pair_with_no_Open_or_Close_call()
    {
        var (window, root, c, link) = Build(lastFolder: null);
        Assert.True(await c.OpenFolderAsync(Garden));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AppScreen.Rank, c.Screen);
        var opens = link.CallCount(nameof(link.OpenAsync));

        window.KeyPress(Key.O, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.NotNull(Find<BrowseView>(root));
        Assert.Equal(@"C:\IMAGES", Find<BrowseView>(root)!.PathLineText);

        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppScreen.Rank, c.Screen);
        Assert.NotNull(Find<RankView>(root));
        Assert.Equal(opens, link.CallCount(nameof(link.OpenAsync)));
        Assert.Equal(0, link.CallCount(nameof(link.CloseAsync)));
    }

    // ---- typing and keys -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Typing_finds_folders_and_O_and_R_are_letters_on_this_screen()
    {
        var (window, root, c, _) = Build(lastFolder: Garden + @"\Ponds");
        window.KeyPress(Key.O, RawInputModifiers.None); // browser at C:\images\garden, Ponds selected
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Garden, c.Browse.Path);

        window.KeyTextInput("ro");
        Dispatcher.UIThread.RunJobs();

        var view = Find<BrowseView>(root)!;
        Assert.Equal("ro", c.Browse.Filter);
        Assert.Equal("ro", view.TypedLineText);
        Assert.Equal("2025 roses", c.Browse.Rows[0].Name);
        Assert.Equal(AppScreen.Browse, c.Screen); // the "o" and "r" did not open or rename anything

        window.KeyPress(Key.Back, RawInputModifiers.None);
        Assert.Equal("r", c.Browse.Filter);
        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Assert.Equal("", c.Browse.Filter);
        Assert.Equal(AppScreen.Browse, c.Screen);
    }

    [AvaloniaFact]
    public void Arrows_move_and_Enter_in_Rename_mode_asks_the_rename_question()
    {
        var (window, _, c, link) = Build(lastFolder: Garden + @"\2025 roses");
        window.KeyPress(Key.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2025 roses", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        window.KeyPress(Key.Down, RawInputModifiers.None);
        Assert.Equal("Ponds", c.Browse.Rows[c.Browse.SelectedIndex].Name);
        window.KeyPress(Key.Up, RawInputModifiers.None);
        window.KeyPress(Key.Enter, RawInputModifiers.None);

        Assert.Equal(AppScreen.Rename, c.Screen);
        Assert.Equal(Garden + @"\2025 roses", c.Rename.Folder);
        Assert.Equal(0, link.CallCount(nameof(link.OpenAsync)));
    }

    [AvaloniaFact]
    public void F1_opens_the_browsers_keys_page_and_any_key_closes_it_without_doing_its_job()
    {
        var (window, root, c, _) = Build();
        window.KeyPress(Key.O, RawInputModifiers.None);
        var view = Find<BrowseView>(root)!;
        Assert.False(view.KeysPageOpen);

        window.KeyPress(Key.F1, RawInputModifiers.None);
        Assert.True(view.KeysPageOpen);
        Assert.True(c.Rank.HelpPinned);

        window.KeyPress(Key.Escape, RawInputModifiers.None); // closes the page; does not leave
        Assert.False(view.KeysPageOpen);
        Assert.Equal(AppScreen.Browse, c.Screen);
    }

    [AvaloniaFact]
    public void The_keys_page_lists_the_browsers_three_columns()
    {
        var (window, root, _, _) = Build();
        window.KeyPress(Key.O, RawInputModifiers.None);
        window.KeyPress(Key.F1, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var page = Find<KeysPage>(Find<BrowseView>(root)!)!;
        var texts = page.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("MOVE", texts);
        Assert.Contains("FOLDERS", texts);
        Assert.Contains("FIND", texts);
        Assert.Contains("Choose this folder", texts);
    }

    // ---- what it shows ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Rows_are_whole_rows_and_the_page_size_follows_the_height()
    {
        var (window, root, c, _) = Build();
        window.KeyPress(Key.O, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var list = Find<BrowseView>(root)!.List;
        Assert.True(list.Fit >= 1);
        Assert.Equal(list.Fit, c.Browse.PageSize);
        Assert.Equal(list.Fit, Math.Floor(list.Bounds.Height / BrowseList.RowHeight));
    }

    [AvaloniaFact]
    public void A_path_too_long_for_the_column_drops_its_earliest_segments_but_keeps_here()
    {
        var deep = @"C:\one\two\three\four\five\six\seven\eight\nine\ten\garden";
        var (window, root, c, link) = Build(lastFolder: deep + @"\x", width: 300);
        link.Listings[deep] = Listing(deep, @"C:\one", Entry(deep, "x"));
        window.KeyPress(Key.O, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var line = Find<BrowseView>(root)!.PathLineText;
        Assert.StartsWith("…\\", line);
        Assert.EndsWith("GARDEN", line);
        Assert.DoesNotContain("C:", line);
    }

    [AvaloniaFact]
    public void An_empty_folder_shows_the_one_faint_line()
    {
        var (window, root, c, link) = Build(lastFolder: Garden + @"\Tools");
        link.Listings[Garden] = Listing(Garden, Images);
        window.KeyPress(Key.O, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var view = Find<BrowseView>(root)!;
        Assert.True(Find<TextBlock>(view, "EmptyLine")!.IsVisible);
        Assert.Equal(BrowseModel.Here, c.Browse.SelectedIndex);
    }

    [AvaloniaFact]
    public void The_Open_pill_on_the_start_screen_opens_the_browser_too()
    {
        var (_, root, c, _) = Build();
        var start = Find<StartView>(root)!;

        Find<Button>(start, "OpenButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(AppScreen.Browse, c.Screen);
    }
}
