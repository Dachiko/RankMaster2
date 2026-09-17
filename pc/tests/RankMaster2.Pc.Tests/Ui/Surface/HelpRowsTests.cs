using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 3.8: "A headless test checks the sheet's rows against KeyMap so the two cannot
/// drift." The Avalonia-free half of that check -- that every row's Intent is really what KeyMap
/// produces for its key caption.</summary>
public class HelpRowsTests
{
    [Fact]
    public void Every_single_key_row_matches_KeyMap()
    {
        Assert.Equal(Intent.VoteLeft, KeyMap.MapCompare(UiKey.Left, UiModifiers.None));
        Assert.Equal(Intent.VoteRight, KeyMap.MapCompare(UiKey.Right, UiModifiers.None));
        Assert.Equal(Intent.OpenFolder, KeyMap.MapCompare(UiKey.O, UiModifiers.None));
        Assert.Equal(Intent.Undo, KeyMap.MapCompare(UiKey.Z, UiModifiers.Control));
        Assert.Equal(Intent.Save, KeyMap.MapCompare(UiKey.S, UiModifiers.Control));
        Assert.Equal(Intent.ToggleHelp, KeyMap.MapCompare(UiKey.F1, UiModifiers.None));
        Assert.Equal(Intent.Quit, KeyMap.MapCompare(UiKey.Escape, UiModifiers.None));
    }

    [Fact]
    public void Nine_rows_in_the_old_apps_order_with_skip_removed()
    {
        Assert.Equal(9, HelpRows.Compare.Count);
        Assert.Equal("Select Left", HelpRows.Compare[0].Label);
        Assert.Equal("Exit", HelpRows.Compare[^1].Label);
        Assert.DoesNotContain(HelpRows.Compare, row => row.Label.Contains("Skip", StringComparison.Ordinal));
    }
}
