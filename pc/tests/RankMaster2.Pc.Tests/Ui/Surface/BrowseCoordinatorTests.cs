using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using Xunit;
using static RankMaster2.Pc.Ui.Tests.Surface.BrowseModelTests;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan I § 4 S2: <see cref="RankCoordinator"/> routes O and R to the browser, feeds it the link's answers one
/// call at a time, and carries out what Enter and Esc mean, against the fake link.</summary>
public class BrowseCoordinatorTests
{
    private const string Images = @"C:\images";
    private const string Garden = @"C:\images\garden";

    private static (RankCoordinator C, FakeSessionLink Link, LastFolderStore Store) Build(string? lastFolder = null)
    {
        var link = new FakeSessionLink { ThrowOnOverlappingBrowseCalls = true };
        var store = new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-browse-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt"));
        var c = new RankCoordinator(link, new FakeStillSource(), null, new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1)),
            new SynchronousUiThread(), new ImmediateDelay(), store);
        if (lastFolder is not null) c.Start.LastFolder = lastFolder;

        link.Roots.Add(new LibraryRoot(@"C:\", "Windows", "fixed", true));
        link.Roots.Add(new LibraryRoot(@"D:\", null, "fixed", true));
        link.Listings[@"C:\"] = Listing(@"C:\", null, Entry(@"C:\", "images", rankable: false));
        link.Listings[Images] = Listing(Images, @"C:\", Entry(Images, "garden", rankable: true), Entry(Images, "boxes", rankable: false));
        link.Listings[Garden] = Listing(Garden, Images,
            Entry(Garden, "2025 roses", rankable: true, hasDatabase: true), Entry(Garden, "Ponds", rankable: true),
            Entry(Garden, "Tools", rankable: false));
        link.Listings[Images + @"\boxes"] = Listing(Images + @"\boxes", Images, Entry(Images + @"\boxes", "b1"));
        return (c, link, store);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "the condition did not become true in 2 s");
    }

    private static int RowIndex(RankCoordinator c, string name) => c.Browse.Rows.ToList().FindIndex(r => r.Name == name);

    // ---- where it starts (plan I § 2.2 item 6) ------------------------------------------------------------------------

    [Fact]
    public void O_on_the_start_screen_starts_in_the_parent_of_the_last_folder_with_it_selected()
    {
        var (c, link, _) = Build(lastFolder: Garden);

        c.OnStartKeyDown(UiKey.O, UiModifiers.None);

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(BrowseMode.Rank, c.Browse.Mode);
        Assert.Equal(Images, c.Browse.Path);
        Assert.Equal("garden", c.Browse.Rows[c.Browse.SelectedIndex].Name);
        Assert.Equal([Images], link.Calls.Where(x => x.Method == nameof(link.BrowseAsync)).Select(x => x.Path));
        Assert.False(c.Browse.Loading);
    }

    [Fact]
    public void With_no_last_folder_the_browser_starts_at_THIS_PC()
    {
        var (c, link, _) = Build();

        c.OnStartKeyDown(UiKey.R, UiModifiers.None);

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(BrowseMode.Rename, c.Browse.Mode);
        Assert.True(c.Browse.AtRoots);
        Assert.Equal(["C:", "D:"], c.Browse.Rows.Select(r => r.Name));
        Assert.Equal(1, link.CallCount(nameof(link.GetRootsAsync)));
    }

    [Fact]
    public void A_last_folder_that_is_a_drive_starts_at_THIS_PC_with_the_drive_selected()
    {
        var (c, _, _) = Build(lastFolder: @"D:\");

        c.OnStartKeyDown(UiKey.O, UiModifiers.None);

        Assert.True(c.Browse.AtRoots);
        Assert.Equal("D:", c.Browse.Rows[c.Browse.SelectedIndex].Name);
    }

    [Fact]
    public void If_the_parent_cannot_be_read_the_browser_falls_back_to_THIS_PC_and_says_why()
    {
        var (c, link, _) = Build(lastFolder: @"E:\gone\x");

        c.OnStartKeyDown(UiKey.O, UiModifiers.None);

        Assert.True(c.Browse.AtRoots);
        Assert.Equal(@"Folder not found · E:\gone", c.Browse.Status);
        Assert.Equal(1, link.MaxBrowseInFlight);
    }

    [Fact]
    public void O_while_a_folder_is_opening_and_R_on_the_compare_screen_do_nothing()
    {
        var (c, _, _) = Build();
        c.Start.BeginOpening(Garden);
        c.OnStartKeyDown(UiKey.O, UiModifiers.None);
        Assert.Equal(AppScreen.Start, c.Screen);

        c.OpenBrowser(BrowseMode.Rename); // still opening
        Assert.Equal(AppScreen.Start, c.Screen);
    }

    // ---- Enter ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Enter_on_an_openable_row_opens_the_folder_and_the_compare_screen_shows()
    {
        var (c, link, _) = Build(lastFolder: Garden);
        link.Snapshot = SnapshotBuilder.Ranking(folder: Garden);
        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot, false));
        c.OpenBrowser(BrowseMode.Rank);
        Assert.Equal("garden", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.OnBrowseKeyDown(UiKey.Enter, UiModifiers.None);
        await WaitFor(() => c.Screen == AppScreen.Rank);

        Assert.Equal(1, link.CallCount(nameof(link.OpenAsync)));
        Assert.Equal(Garden, c.Start.LastFolder);
        Assert.Equal(Garden, c.Rank.Snapshot!.Folder);
    }

    [Fact]
    public async Task A_refused_folder_leaves_the_browser_where_it_was_with_one_accent_line()
    {
        var (c, link, _) = Build(lastFolder: Garden + @"\Ponds");
        link.OpenResults.Enqueue(new OpenResult.Failed(new Failure(FailureKind.FolderNotRankable, "Nothing to rank here", @"C:\images\garden\Ponds has no photos or videos", "folder_not_rankable", null, false)));
        c.OpenBrowser(BrowseMode.Rank);
        Assert.Equal("Ponds", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.OnBrowseKeyDown(UiKey.Enter, UiModifiers.None);
        await WaitFor(() => !c.Browse.Opening);

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(Garden, c.Browse.Path);
        Assert.NotNull(c.Browse.Status);
        Assert.StartsWith("Nothing to rank here", c.Browse.Status);
        Assert.False(c.Browse.Loading);
    }

    [Fact]
    public void In_rename_mode_Enter_on_a_ranked_folder_asks_the_rename_question_without_a_link_call()
    {
        var (c, link, _) = Build(lastFolder: Garden + @"\2025 roses");
        c.OnStartKeyDown(UiKey.R, UiModifiers.None);
        Assert.Equal("2025 roses", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.OnBrowseKeyDown(UiKey.Enter, UiModifiers.None);

        Assert.Equal(AppScreen.Rename, c.Screen);
        Assert.Equal(Garden + @"\2025 roses", c.Rename.Folder);
        Assert.Equal(RenameStage.Confirming, c.Rename.Stage);
        Assert.Equal(0, link.CallCount(nameof(link.OpenAsync)));
        Assert.Equal(0, link.CallCount(nameof(link.StartRenameAsync)));
    }

    [Fact]
    public void In_rename_mode_a_folder_without_a_database_is_gone_into_not_chosen()
    {
        var (c, link, _) = Build(lastFolder: Garden + @"\Ponds");
        c.OnStartKeyDown(UiKey.R, UiModifiers.None);
        Assert.Equal("Ponds", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.OnBrowseKeyDown(UiKey.Enter, UiModifiers.None); // Ponds is rankable but has no database

        Assert.NotEqual(AppScreen.Rename, c.Screen);
        Assert.Contains(link.Calls, x => x.Method == nameof(link.BrowseAsync) && x.Path == Garden + @"\Ponds"); // it tried to list it
    }

    [Fact]
    public void Right_and_Left_go_into_a_folder_and_back_up_selecting_the_one_just_left()
    {
        var (c, _, _) = Build(lastFolder: Images + @"\boxes");
        c.OpenBrowser(BrowseMode.Rank);
        Assert.Equal(Images, c.Browse.Path);

        c.OnBrowseKeyDown(UiKey.Right, UiModifiers.None); // boxes
        Assert.Equal(Images + @"\boxes", c.Browse.Path);

        c.OnBrowseKeyDown(UiKey.Left, UiModifiers.None);
        Assert.Equal(Images, c.Browse.Path);
        Assert.Equal("boxes", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.OnBrowseKeyDown(UiKey.Backspace, UiModifiers.None); // Backspace with nothing typed also goes up
        Assert.Equal(@"C:\", c.Browse.Path);
        c.OnBrowseKeyDown(UiKey.Left, UiModifiers.None);      // from a drive to THIS PC
        Assert.True(c.Browse.AtRoots);
        Assert.Equal("C:", c.Browse.Rows[c.Browse.SelectedIndex].Name);
    }

    // ---- Esc -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Esc_clears_typed_letters_first_then_leaves_for_the_start_screen_without_quitting()
    {
        var (c, link, _) = Build(lastFolder: Garden);
        var quit = false;
        c.QuitRequested += () => quit = true;
        c.OpenBrowser(BrowseMode.Rank);
        c.OnBrowseText("ga");
        Assert.Equal("ga", c.Browse.Filter);

        c.OnBrowseKeyDown(UiKey.Escape, UiModifiers.None);
        Assert.Equal("", c.Browse.Filter);
        Assert.Equal(AppScreen.Browse, c.Screen);

        c.OnBrowseKeyDown(UiKey.Escape, UiModifiers.None);
        Assert.Equal(AppScreen.Start, c.Screen);
        Assert.False(quit);
        Assert.Equal(0, link.CallCount(nameof(link.OpenAsync)));
        Assert.Equal(0, link.CallCount(nameof(link.CloseAsync)));
    }

    [Fact]
    public async Task O_on_the_compare_screen_opens_the_browser_in_the_open_folders_parent_and_Esc_returns_with_the_session_untouched()
    {
        var (c, link, _) = Build();
        link.Snapshot = SnapshotBuilder.Ranking(folder: Garden);
        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot, false));
        Assert.True(await c.OpenFolderAsync(Garden));
        Assert.Equal(AppScreen.Rank, c.Screen);
        var calls = link.Calls.Count;
        var snapshot = c.Rank.Snapshot;

        await c.OnCompareKeyDown(UiKey.O, UiModifiers.None);

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(Images, c.Browse.Path);
        Assert.Equal("garden", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.OnBrowseKeyDown(UiKey.Escape, UiModifiers.None);

        Assert.Equal(AppScreen.Rank, c.Screen);
        Assert.Same(snapshot, c.Rank.Snapshot);
        Assert.Equal(0, link.CallCount(nameof(link.CloseAsync)));
        Assert.Equal(1, link.CallCount(nameof(link.OpenAsync)));                         // only the first open
        Assert.Equal(calls + 1, link.Calls.Count);                                       // plus the one listing
        Assert.Equal(nameof(link.BrowseAsync), link.Calls[^1].Method);
    }

    [Fact]
    public async Task R_on_the_compare_screen_is_not_a_browser_key()
    {
        var (c, link, _) = Build();
        link.Snapshot = SnapshotBuilder.Ranking(folder: Garden);
        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot, false));
        await c.OpenFolderAsync(Garden);

        c.OpenBrowser(BrowseMode.Rename);

        Assert.Equal(AppScreen.Rank, c.Screen);
    }

    // ---- one call at a time ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fast_navigation_never_overlaps_calls_and_ends_showing_the_folder_asked_for_last()
    {
        var (c, link, _) = Build(lastFolder: Garden);
        c.OpenBrowser(BrowseMode.Rank);
        Assert.Equal(Images, c.Browse.Path);
        var held = new TaskCompletionSource();
        link.BeforeBrowseAnswer = path => path == Images + @"\boxes" ? held.Task : Task.CompletedTask;

        c.BrowseSelect(RowIndex(c, "boxes"));
        c.OnBrowseKeyDown(UiKey.Right, UiModifiers.None); // into boxes: the call is held
        Assert.True(c.Browse.Loading);
        c.OnBrowseKeyDown(UiKey.Left, UiModifiers.None);  // up again, before the first listing answers
        Assert.Equal(Images, c.Browse.Path);              // still the old listing: nothing moved

        held.SetResult();
        await WaitFor(() => !c.Browse.Loading);

        Assert.Equal(1, link.MaxBrowseInFlight);          // never two at once (the fake throws otherwise)
        Assert.Equal(@"C:\", c.Browse.Path);              // up from images; `boxes` answered late and was dropped
        Assert.Equal("images", c.Browse.Rows[c.Browse.SelectedIndex].Name);
        Assert.Null(c.Browse.Status);                     // a cancelled call is silent
        Assert.Equal([Images, Images + @"\boxes", @"C:\"], link.Calls.Where(x => x.Method == nameof(link.BrowseAsync)).Select(x => x.Path));
    }

    [Fact]
    public async Task Fast_navigation_that_ends_in_a_new_folder_shows_that_folder()
    {
        var (c, link, _) = Build(lastFolder: Garden);
        c.OpenBrowser(BrowseMode.Rank);
        var held = new TaskCompletionSource();
        link.BeforeBrowseAnswer = path => path == @"C:\" ? held.Task : Task.CompletedTask;

        c.OnBrowseKeyDown(UiKey.Left, UiModifiers.None);  // up to C:\ : held
        c.OnBrowseKeyDown(UiKey.Right, UiModifiers.None); // → on the old listing's selected row (garden): goes there next
        held.SetResult();
        await WaitFor(() => !c.Browse.Loading);

        Assert.Equal(1, link.MaxBrowseInFlight);
        Assert.Equal(Garden, c.Browse.Path);
    }

    [Fact]
    public async Task Enter_while_a_listing_is_in_flight_waits_for_it_before_calling_open()
    {
        var (c, link, _) = Build(lastFolder: Garden + @"\Ponds");
        link.Snapshot = SnapshotBuilder.Ranking(folder: Garden + @"\Ponds");
        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot, false));
        c.OpenBrowser(BrowseMode.Rank);
        var held = new TaskCompletionSource();
        link.BeforeBrowseAnswer = path => path == Images ? held.Task : Task.CompletedTask;

        c.OnBrowseKeyDown(UiKey.Left, UiModifiers.None);  // up to images: held
        c.OnBrowseKeyDown(UiKey.Enter, UiModifiers.None); // Enter on the old listing's selected folder (Ponds)
        Assert.Equal(0, link.CallCount(nameof(link.OpenAsync))); // the link's gate is busy: open waits

        held.SetResult();
        await WaitFor(() => c.Screen == AppScreen.Rank);

        Assert.Equal(1, link.CallCount(nameof(link.OpenAsync)));
    }

    // ---- failures ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_folder_that_is_gone_says_so_in_one_line_and_the_browser_stays()
    {
        var (c, link, _) = Build(lastFolder: Garden);
        c.OpenBrowser(BrowseMode.Rank);
        link.Listings.Remove(Images + @"\boxes");
        c.BrowseSelect(RowIndex(c, "boxes"));

        c.OnBrowseKeyDown(UiKey.Right, UiModifiers.None);

        Assert.Equal(Images, c.Browse.Path);
        Assert.Equal(@"Folder not found · C:\images\boxes", c.Browse.Status);
        Assert.False(c.Browse.Loading);
    }

    [Fact]
    public void A_server_that_does_not_answer_reads_Server_not_answering()
    {
        var (c, link, _) = Build();
        link.RootsResults.Enqueue(new RootsResult.Failed(new Failure(FailureKind.ServerNotRunning, "Server not running", "", "server_not_running", null, false)));

        c.OnStartKeyDown(UiKey.O, UiModifiers.None);

        Assert.Equal(AppScreen.Browse, c.Screen);
        Assert.Equal(StartModel.ServerNotAnswering, c.Browse.Status);
        Assert.False(c.Browse.Loading);
    }

    [Fact]
    public void Typing_on_the_browser_filters_and_does_nothing_with_the_keys_page_open()
    {
        var (c, _, _) = Build(lastFolder: Garden + @"\Tools");
        c.OpenBrowser(BrowseMode.Rank);

        c.OnBrowseText("po");
        Assert.Equal("Ponds", c.Browse.Rows[0].Name);

        c.OnBrowseKeyDown(UiKey.F1, UiModifiers.None);
        Assert.True(c.Rank.HelpPinned);
        c.OnBrowseText("x");
        Assert.Equal("po", c.Browse.Filter);

        c.OnBrowseKeyDown(UiKey.Escape, UiModifiers.None); // closes the page, does not leave
        Assert.False(c.Rank.HelpPinned);
        Assert.Equal(AppScreen.Browse, c.Screen);
    }

    [Fact]
    public void A_double_click_is_Enter_on_that_row_and_a_click_on_a_path_segment_goes_there()
    {
        var (c, _, _) = Build(lastFolder: Garden);
        c.OpenBrowser(BrowseMode.Rank);

        c.BrowseGoToSegment(c.Browse.Segments[0]); // C:
        Assert.Equal(@"C:\", c.Browse.Path);
        Assert.Equal("images", c.Browse.Rows[c.Browse.SelectedIndex].Name);

        c.BrowseActivate(RowIndex(c, "images")); // not openable: goes into it
        Assert.Equal(Images, c.Browse.Path);
    }
}
