using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 6.1 "KeyMap": every row of § 1.1 including numpad; Ctrl+S is save and never
/// skip; S alone is skip; unknown keys → None; the start-screen map differs from the compare map
/// exactly as § 1.1 says.</summary>
public class KeyMapTests
{
    [Theory]
    [InlineData(UiKey.Left, Intent.VoteLeft)]
    [InlineData(UiKey.Right, Intent.VoteRight)]
    [InlineData(UiKey.Down, Intent.Skip)]
    [InlineData(UiKey.S, Intent.Skip)]
    [InlineData(UiKey.D1, Intent.DiscardLeft)]
    [InlineData(UiKey.NumPad1, Intent.DiscardLeft)]
    [InlineData(UiKey.D2, Intent.DiscardRight)]
    [InlineData(UiKey.NumPad2, Intent.DiscardRight)]
    [InlineData(UiKey.D4, Intent.SpecialLeft)]
    [InlineData(UiKey.NumPad4, Intent.SpecialLeft)]
    [InlineData(UiKey.D5, Intent.SpecialRight)]
    [InlineData(UiKey.NumPad5, Intent.SpecialRight)]
    [InlineData(UiKey.O, Intent.OpenFolder)]
    [InlineData(UiKey.F1, Intent.ToggleHelp)]
    [InlineData(UiKey.Escape, Intent.Quit)]
    [InlineData(UiKey.D3, Intent.None)]
    [InlineData(UiKey.NumPad3, Intent.None)]
    [InlineData(UiKey.Up, Intent.None)]
    [InlineData(UiKey.Enter, Intent.None)]
    [InlineData(UiKey.Space, Intent.None)]
    [InlineData(UiKey.None, Intent.None)]
    public void Compare_map_with_no_modifiers(UiKey key, Intent expected) =>
        Assert.Equal(expected, KeyMap.MapCompare(key, UiModifiers.None));

    [Fact]
    public void Ctrl_S_is_save_never_skip()
    {
        Assert.Equal(Intent.Save, KeyMap.MapCompare(UiKey.S, UiModifiers.Control));
        Assert.NotEqual(Intent.Skip, KeyMap.MapCompare(UiKey.S, UiModifiers.Control));
    }

    [Fact]
    public void S_alone_is_skip() => Assert.Equal(Intent.Skip, KeyMap.MapCompare(UiKey.S, UiModifiers.None));

    [Fact]
    public void Ctrl_Z_is_undo_but_Z_alone_is_nothing()
    {
        Assert.Equal(Intent.Undo, KeyMap.MapCompare(UiKey.Z, UiModifiers.Control));
        Assert.Equal(Intent.None, KeyMap.MapCompare(UiKey.Z, UiModifiers.None));
    }

    [Fact]
    public void Shift_does_not_change_vote_or_skip()
    {
        Assert.Equal(Intent.VoteRight, KeyMap.MapCompare(UiKey.Right, UiModifiers.Shift));
        Assert.Equal(Intent.VoteLeft, KeyMap.MapCompare(UiKey.Left, UiModifiers.Shift | UiModifiers.Alt));
        Assert.Equal(Intent.Skip, KeyMap.MapCompare(UiKey.S, UiModifiers.Shift));
    }

    [Fact]
    public void Alt_does_not_change_anything_named_in_the_table()
    {
        Assert.Equal(Intent.DiscardLeft, KeyMap.MapCompare(UiKey.D1, UiModifiers.Alt));
        Assert.Equal(Intent.OpenFolder, KeyMap.MapCompare(UiKey.O, UiModifiers.Alt));
    }

    // ---- start screen ------------------------------------------------------------------------------

    [Theory]
    [InlineData(UiKey.O, Intent.OpenFolder)]
    [InlineData(UiKey.F1, Intent.ToggleHelp)]
    [InlineData(UiKey.Escape, Intent.Quit)]
    public void Start_map_shares_open_help_quit_with_compare(UiKey key, Intent expected) =>
        Assert.Equal(expected, KeyMap.MapStart(key, UiModifiers.None));

    [Fact]
    public void Start_map_Ctrl_Z_is_undo()
    {
        Assert.Equal(Intent.Undo, KeyMap.MapStart(UiKey.Z, UiModifiers.Control));
        Assert.Equal(Intent.None, KeyMap.MapStart(UiKey.Z, UiModifiers.None));
    }

    [Theory]
    [InlineData(UiKey.Left)]
    [InlineData(UiKey.Right)]
    [InlineData(UiKey.Down)]
    [InlineData(UiKey.S)]
    [InlineData(UiKey.D1)]
    [InlineData(UiKey.D2)]
    [InlineData(UiKey.D4)]
    [InlineData(UiKey.D5)]
    public void Start_map_differs_from_compare_map_for_ranking_only_keys(UiKey key)
    {
        // These mean something on the compare screen but nothing on the start screen -- Enter/Space
        // reach a focused button through Avalonia's own behaviour, never through KeyMap (plan § 3.7).
        Assert.Equal(Intent.None, KeyMap.MapStart(key, UiModifiers.None));
        Assert.NotEqual(Intent.None, KeyMap.MapCompare(key, UiModifiers.None));
    }
}
