using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan I § 4 S2's list for <see cref="BrowseModel"/> (no Avalonia, no link): what a key means, what a row
/// is, how typing reorders, which answers are dropped.</summary>
public class BrowseModelTests
{
    private const string Garden = @"C:\images\garden";

    internal static FolderEntry Entry(string parent, string name, bool? rankable = true, bool hasDatabase = false, bool accessible = true) =>
        new(name, parent.TrimEnd('\\') + "\\" + name, rankable, hasDatabase, accessible);

    internal static FolderListing Listing(string path, string? parent, params FolderEntry[] entries) => new(path, parent, entries);

    private static BrowseModel Landed(BrowseMode mode, FolderListing listing, string? select = null, bool? hereOpenable = null)
    {
        var model = new BrowseModel();
        model.Reset(mode);
        var seq = model.BeginLoad(listing.Path, select, hereOpenable);
        Assert.True(model.ApplyListing(seq, listing));
        return model;
    }

    private static FolderListing GardenListing() => Listing(Garden, @"C:\images",
        Entry(Garden, "Winter"), Entry(Garden, "2025 roses"), Entry(Garden, "Ponds"),
        Entry(Garden, "Tools and sheds", rankable: false), Entry(Garden, "2024 spring"), Entry(Garden, "Vegetables"));

    // ---- rows: names, order, dimming ------------------------------------------------------------------------------

    [Fact]
    public void Rows_are_in_natural_order_so_Day_9_comes_before_Day_10()
    {
        var model = Landed(BrowseMode.Rank, Listing(@"C:\t", @"C:\",
            Entry(@"C:\t", "Day 10"), Entry(@"C:\t", "day 2"), Entry(@"C:\t", "Day 9"), Entry(@"C:\t", "Day 1")));

        Assert.Equal(["Day 1", "day 2", "Day 9", "Day 10"], model.Rows.Select(r => r.Name));
    }

    [Theory]
    [InlineData("a9", "a10", -1)]
    [InlineData("a10", "a9", 1)]
    [InlineData("a", "A", 1)]       // equal-looking names fall back to the raw ordinal ('a' > 'A')
    [InlineData("a1", "a01", 1)]
    [InlineData("b", "a10", 1)]
    [InlineData("a", "a1", -1)]
    public void Natural_order_ports_the_phones_rules(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(NaturalOrder.Instance.Compare(a, b)));

    [Fact]
    public void Hidden_and_system_folders_are_left_out_by_name()
    {
        var model = Landed(BrowseMode.Rank, Listing(@"C:\", null,
            Entry(@"C:\", "Photos"), Entry(@"C:\", ".git"), Entry(@"C:\", "$RECYCLE.BIN"), Entry(@"C:\", "$Recycle"),
            Entry(@"C:\", "System Volume Information"), Entry(@"C:\", "system volume information"), Entry(@"C:\", "System")));

        Assert.Equal(["Photos", "System"], model.Rows.Select(r => r.Name));
    }

    [Fact]
    public void A_row_is_dimmed_when_the_mode_cannot_act_on_it()
    {
        var listing = Listing(Garden, @"C:\images",
            Entry(Garden, "ranks", rankable: true, hasDatabase: true),
            Entry(Garden, "fresh", rankable: true, hasDatabase: false),
            Entry(Garden, "empty", rankable: false),
            Entry(Garden, "unknown", rankable: null, accessible: false));

        var rank = Landed(BrowseMode.Rank, listing);
        Assert.Equal(["empty", "fresh", "ranks", "unknown"], rank.Rows.Select(r => r.Name));
        Assert.Equal([true, false, false, true], rank.Rows.Select(r => r.Dimmed)); // empty, fresh, ranks, unknown

        // Rename: rankable && hasDatabase.
        var rename = Landed(BrowseMode.Rename, listing);
        Assert.Equal([true, true, false, true], rename.Rows.Select(r => r.Dimmed));
        Assert.Equal(["ranks"], rename.Rows.Where(r => r.Openable).Select(r => r.Name));
    }

    [Fact]
    public void The_roots_are_drive_names_and_one_that_is_not_available_is_dimmed()
    {
        var model = new BrowseModel();
        model.Reset(BrowseMode.Rank);
        var seq = model.BeginLoad(null);
        model.ApplyRoots(seq, [new LibraryRoot(@"C:\", "Windows", "fixed", true), new LibraryRoot(@"D:\", null, "fixed", true),
            new LibraryRoot(@"E:\", null, "removable", false), new LibraryRoot(@"\\nas\media", null, "network", true)]);

        Assert.True(model.AtRoots);
        Assert.Equal(["C:", "D:", "E:", @"\\nas\media"], model.Rows.Select(r => r.Name));
        Assert.Equal([false, false, true, false], model.Rows.Select(r => r.Dimmed));
        Assert.Equal([new PathSegment("THIS PC", "")], model.Segments);
        Assert.Equal("ENTER / → INTO · ESC BACK", model.KeyHint);
    }

    // ---- where it starts / selecting --------------------------------------------------------------------------------

    [Fact]
    public void A_listing_selects_the_folder_asked_for_else_the_first_row_else_here()
    {
        var withSelect = Landed(BrowseMode.Rank, GardenListing(), select: Garden + @"\Ponds");
        Assert.Equal("Ponds", withSelect.Rows[withSelect.SelectedIndex].Name);

        var without = Landed(BrowseMode.Rank, GardenListing());
        Assert.Equal(0, without.SelectedIndex);

        var empty = Landed(BrowseMode.Rank, Listing(Garden, @"C:\images"));
        Assert.Equal(BrowseModel.Here, empty.SelectedIndex);
        Assert.True(empty.ShowsEmptyLine);
    }

    [Fact]
    public void Up_from_the_first_row_selects_here_and_down_goes_back_to_the_first_row()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        Assert.Equal(0, model.SelectedIndex);

        model.MoveUp();
        Assert.Equal(BrowseModel.Here, model.SelectedIndex);
        model.MoveUp();
        Assert.Equal(BrowseModel.Here, model.SelectedIndex); // stays

        model.MoveDown();
        Assert.Equal(0, model.SelectedIndex);
        model.End();
        Assert.Equal(5, model.SelectedIndex);
        model.MoveDown();
        Assert.Equal(5, model.SelectedIndex);
        model.Home();
        Assert.Equal(0, model.SelectedIndex);
    }

    [Fact]
    public void Pages_move_by_the_page_size_and_never_past_the_ends()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.PageSize = 4;

        model.PageDown();
        Assert.Equal(4, model.SelectedIndex);
        model.PageDown();
        Assert.Equal(5, model.SelectedIndex);
        model.PageUp();
        Assert.Equal(1, model.SelectedIndex);
        model.PageUp();
        Assert.Equal(BrowseModel.Here, model.SelectedIndex);
    }

    [Fact]
    public void Down_from_here_in_an_empty_folder_stays_on_here()
    {
        var model = Landed(BrowseMode.Rank, Listing(Garden, @"C:\images"));
        model.MoveDown();
        Assert.Equal(BrowseModel.Here, model.SelectedIndex);
    }

    // ---- Enter, →, ← ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Enter_on_an_openable_row_chooses_it_and_on_one_that_is_not_goes_into_it()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.Select(model.Rows.ToList().FindIndex(r => r.Name == "Ponds"));
        Assert.Equal(new BrowseAction(BrowseActionKind.Rank, Garden + @"\Ponds"), model.Activate());

        model.Select(model.Rows.ToList().FindIndex(r => r.Name == "Tools and sheds"));
        var into = model.Activate();
        Assert.Equal(BrowseActionKind.Go, into.Kind);
        Assert.Equal(Garden + @"\Tools and sheds", into.Path);
        Assert.False(into.Roots);
    }

    [Fact]
    public void Enter_on_a_folder_that_cannot_be_read_does_nothing_and_so_does_Enter_on_a_missing_drive()
    {
        var model = Landed(BrowseMode.Rank, Listing(Garden, @"C:\images", Entry(Garden, "locked", rankable: null, accessible: false)));
        Assert.Equal(BrowseAction.None, model.Activate());
        Assert.Equal(BrowseAction.None, model.GoInto());

        var roots = new BrowseModel();
        roots.Reset(BrowseMode.Rank);
        roots.ApplyRoots(roots.BeginLoad(null), [new LibraryRoot(@"E:\", null, null, false)]);
        Assert.Equal(BrowseAction.None, roots.Activate());
    }

    [Fact]
    public void In_rename_mode_Enter_chooses_only_a_folder_that_is_rankable_and_has_a_database()
    {
        var model = Landed(BrowseMode.Rename, Listing(Garden, @"C:\images",
            Entry(Garden, "a ranked", hasDatabase: true), Entry(Garden, "b fresh", hasDatabase: false)));

        Assert.Equal(new BrowseAction(BrowseActionKind.Rename, Garden + @"\a ranked"), model.Activate());
        model.MoveDown();
        Assert.Equal(BrowseActionKind.Go, model.Activate().Kind);
    }

    [Fact]
    public void Enter_on_here_chooses_the_folder_being_shown_unless_the_listing_it_came_from_said_no()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.MoveUp();
        Assert.Equal(BrowseModel.Here, model.SelectedIndex);
        Assert.Equal(new BrowseAction(BrowseActionKind.Rank, Garden), model.Activate());

        var known = Landed(BrowseMode.Rank, GardenListing(), hereOpenable: false);
        known.Select(BrowseModel.Here);
        Assert.Equal(BrowseAction.None, known.Activate());

        var roots = new BrowseModel();
        roots.Reset(BrowseMode.Rank);
        roots.ApplyRoots(roots.BeginLoad(null), [new LibraryRoot(@"C:\", null, null, true)]);
        roots.Select(BrowseModel.Here);
        Assert.Equal(BrowseAction.None, roots.Activate()); // THIS PC is not a folder
    }

    [Fact]
    public void Right_goes_into_the_selected_folder_and_remembers_whether_it_can_be_chosen()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.Select(model.Rows.ToList().FindIndex(r => r.Name == "Tools and sheds"));
        var into = model.GoInto();
        Assert.Equal(new BrowseAction(BrowseActionKind.Go, Garden + @"\Tools and sheds", HereOpenable: false), into);

        model.MoveUp();
        model.Select(BrowseModel.Here);
        Assert.Equal(BrowseAction.None, model.GoInto()); // nothing to go into from here
    }

    [Fact]
    public void Left_goes_up_selecting_the_folder_just_left_and_from_a_drive_goes_to_THIS_PC()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        Assert.Equal(new BrowseAction(BrowseActionKind.Go, @"C:\images", Select: Garden), model.GoUp());

        var drive = Landed(BrowseMode.Rank, Listing(@"C:\", null, Entry(@"C:\", "images")));
        var up = drive.GoUp();
        Assert.Equal(BrowseActionKind.Go, up.Kind);
        Assert.True(up.Roots);
        Assert.Equal(@"C:\", up.Select);

        var roots = new BrowseModel();
        roots.Reset(BrowseMode.Rank);
        roots.ApplyRoots(roots.BeginLoad(null), [new LibraryRoot(@"C:\", null, null, true)]);
        Assert.Equal(BrowseAction.None, roots.GoUp()); // nowhere above THIS PC
    }

    [Fact]
    public void A_click_on_a_path_segment_goes_there_and_on_here_selects_it()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        var segments = model.Segments;
        Assert.Equal(["C:", "images", "garden"], segments.Select(s => s.Text));

        var go = model.GoToSegment(segments[0]);
        Assert.Equal(new BrowseAction(BrowseActionKind.Go, @"C:\", Select: @"C:\images"), go);

        Assert.Equal(BrowseAction.None, model.GoToSegment(segments[2]));
        Assert.Equal(BrowseModel.Here, model.SelectedIndex);
    }

    // ---- typing to find -------------------------------------------------------------------------------------------------

    [Fact]
    public void Typing_moves_matches_to_the_top_dims_the_rest_and_selects_the_best_match()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        Assert.Equal(["2024 spring", "2025 roses", "Ponds", "Tools and sheds", "Vegetables", "Winter"], model.Rows.Select(r => r.Name));

        model.Type("ro");

        Assert.Equal("ro", model.Filter);
        Assert.Equal("2025 roses", model.Rows[0].Name);
        Assert.Equal(1, model.MatchCount);
        Assert.Equal(5, model.Rows.Count(r => r.Far)); // the rest stay, dimmed, in their order
        Assert.Equal(["2025 roses", "2024 spring", "Ponds", "Tools and sheds", "Vegetables", "Winter"], model.Rows.Select(r => r.Name));
        Assert.Equal(0, model.SelectedIndex);
        Assert.Equal((5, 2), (model.Rows[0].MatchStart, model.Rows[0].MatchLength));
    }

    [Fact]
    public void A_name_that_starts_with_the_letters_is_the_best_match()
    {
        var model = Landed(BrowseMode.Rank, Listing(Garden, null, Entry(Garden, "Backup photos"), Entry(Garden, "Photos")));
        model.Type("pho");
        Assert.Equal(["Photos", "Backup photos"], model.Rows.Take(2).Select(r => r.Name));
    }

    [Fact]
    public void Backspace_deletes_a_letter_and_Esc_clears_them_and_keeps_the_selected_folder()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.Type("ro");
        model.Type("s");
        Assert.Equal("ros", model.Filter);

        Assert.True(model.Backspace());
        Assert.Equal("ro", model.Filter);

        Assert.True(model.ClearFilter());
        Assert.Equal("", model.Filter);
        Assert.All(model.Rows, r => Assert.False(r.Far));
        Assert.Equal("2025 roses", model.Rows[model.SelectedIndex].Name); // the best match stays selected
        Assert.Equal(["2024 spring", "2025 roses", "Ponds", "Tools and sheds", "Vegetables", "Winter"], model.Rows.Select(r => r.Name));

        Assert.False(model.ClearFilter()); // nothing typed: Esc is the caller's (leave)
        Assert.False(model.Backspace());   // nothing typed: Backspace is the caller's (go up)
    }

    [Fact]
    public void Typing_with_no_match_selects_nothing_and_Enter_does_nothing()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.Type("zzz");

        Assert.Equal(BrowseModel.Nothing, model.SelectedIndex);
        Assert.Equal(6, model.Rows.Count); // filtering reorders, it never empties
        Assert.Equal(BrowseAction.None, model.Activate());

        model.MoveDown();
        Assert.Equal(0, model.SelectedIndex);
    }

    [Fact]
    public void Control_characters_and_a_leading_space_are_not_typed()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.Type(" \b\r");
        Assert.Equal("", model.Filter);
        model.Type("to");
        model.Type(" s");
        Assert.Equal("to s", model.Filter);
    }

    [Fact]
    public void A_new_listing_clears_the_typed_letters()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        model.Type("ro");
        var seq = model.BeginLoad(@"C:\images");
        model.ApplyListing(seq, Listing(@"C:\images", @"C:\", Entry(@"C:\images", "garden")));
        Assert.Equal("", model.Filter);
    }

    // ---- loading: stale answers, failures ---------------------------------------------------------------------------------

    [Fact]
    public void An_answer_for_a_folder_already_left_is_dropped_by_its_sequence_number()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());

        var first = model.BeginLoad(@"C:\images\garden\Ponds");
        var second = model.BeginLoad(@"C:\images");
        Assert.True(model.Loading);

        Assert.False(model.ApplyListing(first, Listing(@"C:\images\garden\Ponds", Garden, Entry(@"C:\images\garden\Ponds", "late"))));
        Assert.Equal(Garden, model.Path);            // nothing changed
        Assert.True(model.Loading);                  // still waiting for the second
        Assert.False(model.Fail(first, "stale failure"));
        Assert.Null(model.Status);

        Assert.True(model.ApplyListing(second, Listing(@"C:\images", @"C:\", Entry(@"C:\images", "garden"))));
        Assert.Equal(@"C:\images", model.Path);
        Assert.False(model.Loading);
    }

    [Fact]
    public void Loading_keeps_the_rows_and_the_path_until_the_new_listing_lands()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        var rows = model.Rows;

        model.BeginLoad(@"C:\images\garden\Ponds");

        Assert.True(model.Loading);
        Assert.Same(rows, model.Rows);
        Assert.Equal(Garden, model.Path);
    }

    [Fact]
    public void A_failure_keeps_the_browser_where_it_was_and_sets_the_status_line()
    {
        var model = Landed(BrowseMode.Rank, GardenListing());
        var seq = model.BeginLoad(@"C:\gone");

        Assert.True(model.Fail(seq, "Folder not found"));

        Assert.False(model.Loading);
        Assert.Equal("Folder not found", model.Status);
        Assert.Equal(Garden, model.Path);
        Assert.Equal(6, model.Rows.Count);

        model.BeginLoad(Garden); // the next navigation clears it
        Assert.Null(model.Status);
    }

    [Fact]
    public void Before_the_first_listing_the_path_line_shows_the_folder_being_asked_for()
    {
        var model = new BrowseModel();
        model.Reset(BrowseMode.Rank);
        model.BeginLoad(@"C:\images", Garden);

        Assert.False(model.Landed);
        Assert.Equal(["C:", "images"], model.Segments.Select(s => s.Text));
        Assert.False(model.ShowsEmptyLine);
    }

    // ---- the path line ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\images\garden", @"C:\images")]
    [InlineData(@"C:\images", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData(@"\\nas\media\x", @"\\nas\media\")]
    [InlineData(@"\\nas\media", null)]
    [InlineData("/lib/photos", "/lib")]
    [InlineData("/lib", "/")]
    [InlineData("/", null)]
    public void The_parent_of_a_path(string path, string? parent) => Assert.Equal(parent, BrowsePath.ParentOf(path));

    [Fact]
    public void A_path_splits_into_segments_that_each_know_where_a_click_goes()
    {
        Assert.Equal([("C:", @"C:\"), ("images", @"C:\images"), ("garden", Garden)],
            BrowsePath.Split(Garden).Select(s => (s.Text, s.Target)));
        Assert.Equal([(@"\\nas\media", @"\\nas\media\"), ("x", @"\\nas\media\x")],
            BrowsePath.Split(@"\\nas\media\x").Select(s => (s.Text, s.Target)));
    }

    [Fact]
    public void A_long_path_drops_its_earliest_segments_behind_an_ellipsis_and_here_always_stays()
    {
        var segments = BrowsePath.Split(@"C:\one\two\three\four\five");

        var wide = BrowsePath.Elide(segments, p => BrowsePath.Length(p) <= 100);
        Assert.False(wide.Elided);
        Assert.Equal(6, wide.Shown.Count);

        var narrow = BrowsePath.Elide(segments, p => BrowsePath.Length(p) <= 16);
        Assert.True(narrow.Elided);
        Assert.Equal(["four", "five"], narrow.Shown.Select(s => s.Text)); // "…ourive" is 11; with "three" it would be 17
        Assert.True(BrowsePath.Length(narrow) <= 16);

        var tiny = BrowsePath.Elide(segments, _ => false);
        Assert.True(tiny.Elided);
        Assert.Equal(["five"], tiny.Shown.Select(s => s.Text)); // even when nothing fits, here shows
    }

    [Fact]
    public void A_single_segment_path_is_never_elided()
    {
        var one = BrowsePath.Elide(BrowsePath.Split(@"C:\"), _ => false);
        Assert.False(one.Elided);
        Assert.Equal(["C:"], one.Shown.Select(s => s.Text));
    }
}
