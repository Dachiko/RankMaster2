using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan I § 2.2 item 5: the browser's key map, and its keys page kept in step with it.</summary>
public class BrowseKeysTests
{
    [Theory]
    [InlineData(UiKey.Up, Intent.BrowseUp)]
    [InlineData(UiKey.Down, Intent.BrowseDown)]
    [InlineData(UiKey.PageUp, Intent.BrowsePageUp)]
    [InlineData(UiKey.PageDown, Intent.BrowsePageDown)]
    [InlineData(UiKey.Home, Intent.BrowseHome)]
    [InlineData(UiKey.End, Intent.BrowseEnd)]
    [InlineData(UiKey.Right, Intent.BrowseInto)]
    [InlineData(UiKey.Left, Intent.BrowseOut)]
    [InlineData(UiKey.Backspace, Intent.BrowseBackspace)]
    [InlineData(UiKey.Enter, Intent.BrowseEnter)]
    [InlineData(UiKey.Escape, Intent.BrowseLeave)]
    [InlineData(UiKey.F1, Intent.ToggleHelp)]
    // Letters type into the filter on this screen, so O and R mean nothing here; nor do the compare screen's keys.
    [InlineData(UiKey.O, Intent.None)]
    [InlineData(UiKey.R, Intent.None)]
    [InlineData(UiKey.S, Intent.None)]
    [InlineData(UiKey.Z, Intent.None)]
    [InlineData(UiKey.D1, Intent.None)]
    [InlineData(UiKey.NumPad4, Intent.None)]
    [InlineData(UiKey.Space, Intent.None)]
    [InlineData(UiKey.None, Intent.None)]
    public void Browse_map_with_no_modifiers(UiKey key, Intent expected) =>
        Assert.Equal(expected, KeyMap.MapBrowse(key, UiModifiers.None));

    [Fact]
    public void Modifiers_do_not_change_what_a_browse_key_means()
    {
        Assert.Equal(Intent.BrowseDown, KeyMap.MapBrowse(UiKey.Down, UiModifiers.Control));
        Assert.Equal(Intent.None, KeyMap.MapBrowse(UiKey.Z, UiModifiers.Control)); // no Ctrl+Z here
    }

    private static UiKey KeyOf(string caption) => caption switch
    {
        "↑" => UiKey.Up,
        "→" => UiKey.Right,
        "←" => UiKey.Left,
        "PgUp" => UiKey.PageUp,
        "Home" => UiKey.Home,
        "Enter" => UiKey.Enter,
        "Backspace" => UiKey.Backspace,
        "Esc" => UiKey.Escape,
        "F1" => UiKey.F1,
        "A–Z" => UiKey.None, // typed text, not a key
        _ => throw new InvalidOperationException("Unrecognised key caption '" + caption + "'."),
    };

    /// <summary>Each row's first chip names the key <see cref="KeyMap.MapBrowse"/> maps to the row's Intent, so the keys
    /// page cannot drift from the map. The typing row is None on purpose.</summary>
    [Fact]
    public void Every_row_of_the_browsers_keys_page_matches_MapBrowse()
    {
        foreach (var row in HelpRows.BrowsePage.SelectMany(s => s.Rows))
            Assert.Equal(row.Intent, KeyMap.MapBrowse(KeyOf(row.Chips[0]), UiModifiers.None));
    }

    [Fact]
    public void The_browsers_keys_page_has_three_numbered_columns()
    {
        Assert.Equal(["01", "02", "03"], HelpRows.BrowsePage.Select(c => c.Number));
        Assert.Equal(["MOVE", "FOLDERS", "FIND"], HelpRows.BrowsePage.Select(c => c.Heading));
        Assert.Equal(["Find by typing"], HelpRows.BrowsePage.SelectMany(s => s.Rows).Where(r => r.Intent == Intent.None).Select(r => r.Label));
    }

    [Fact]
    public void The_compare_and_start_maps_are_unchanged_by_the_browser_keys()
    {
        Assert.Equal(Intent.OpenFolder, KeyMap.MapCompare(UiKey.O, UiModifiers.None));
        Assert.Equal(Intent.OpenFolder, KeyMap.MapStart(UiKey.O, UiModifiers.None));
        Assert.Equal(Intent.RenameFolder, KeyMap.MapStart(UiKey.R, UiModifiers.None));
        Assert.Equal(Intent.None, KeyMap.MapCompare(UiKey.Backspace, UiModifiers.None));
        Assert.Equal(Intent.None, KeyMap.MapStart(UiKey.Home, UiModifiers.None));
    }
}
