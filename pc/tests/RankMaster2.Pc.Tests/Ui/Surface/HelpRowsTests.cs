using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 3.8: "A headless test checks the sheet's rows against KeyMap so the two cannot
/// drift." The Avalonia-free half of that check -- that every row's Intent is really what KeyMap
/// produces for its key caption.
/// <para/>
/// <b>Second audit, § 5.</b> The method below used to never read <see cref="HelpRows"/> at all --
/// it re-asserted <see cref="KeyMap"/> against hard-coded expectations that happened to describe
/// <see cref="HelpRows.Compare"/>'s current captions, so changing a caption in
/// <see cref="HelpRows"/> (its header's whole reason to exist -- "so the two cannot drift") left
/// both this test and <see cref="Nine_rows_in_the_old_apps_order_with_skip_removed"/> green. It
/// now parses every row's own <see cref="HelpRow.Keys"/> caption and feeds the result straight
/// into <see cref="KeyMap.MapCompare"/>, so a caption that no longer names the key <see
/// cref="KeyMap"/> actually maps to <see cref="HelpRow.Intent"/> -- including one that still
/// parses as a different, valid key ("Ctrl+Z" changed to "Ctrl+S") -- fails here.</summary>
public class HelpRowsTests
{
    [Fact]
    public void Every_single_key_row_matches_KeyMap()
    {
        foreach (var row in HelpRows.Compare)
        {
            var (key, modifiers) = ParsePrimaryKey(row.Keys);
            Assert.Equal(row.Intent, KeyMap.MapCompare(key, modifiers));
        }
    }

    /// <summary>
    /// Two-key rows ("1 / 2", "4 / 5") record only the Left-side Intent (plan's own design, per
    /// HelpRows.cs's comments), so only the caption's first token -- before any " / " -- is what a
    /// single <see cref="HelpRow.Intent"/> can be checked against.
    /// </summary>
    private static (UiKey Key, UiModifiers Modifiers) ParsePrimaryKey(string caption)
    {
        var primary = caption.Split(" / ")[0].Trim();
        var keyToken = primary;
        var modifiers = UiModifiers.None;

        if (primary.Contains('+'))
        {
            var parts = primary.Split('+');
            keyToken = parts[^1];
            foreach (var modifierToken in parts[..^1])
            {
                modifiers |= modifierToken switch
                {
                    "Ctrl" => UiModifiers.Control,
                    "Shift" => UiModifiers.Shift,
                    "Alt" => UiModifiers.Alt,
                    _ => throw new InvalidOperationException(
                        $"Unrecognised modifier '{modifierToken}' in help caption '{caption}'."),
                };
            }
        }

        var key = keyToken switch
        {
            "←" => UiKey.Left,
            "→" => UiKey.Right,
            "↑" => UiKey.Up,
            "↓" => UiKey.Down,
            "O" => UiKey.O,
            "Z" => UiKey.Z,
            "S" => UiKey.S,
            "F1" => UiKey.F1,
            "Esc" => UiKey.Escape,
            "1" => UiKey.D1,
            "2" => UiKey.D2,
            "3" => UiKey.D3,
            "4" => UiKey.D4,
            "5" => UiKey.D5,
            _ => throw new InvalidOperationException(
                $"Unrecognised key token '{keyToken}' in help caption '{caption}'."),
        };

        return (key, modifiers);
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
