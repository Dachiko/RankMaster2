namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// The help sheet's rows (plan § 3.8), in the old app's order, as plain data so
/// <c>Views/HelpSheet</c> renders them and a headless test can check them against
/// <see cref="KeyMap"/> without the two drifting apart.
/// </summary>
public sealed record HelpRow(string Label, string Keys, Intent Intent);

public static class HelpRows
{
    public static readonly IReadOnlyList<HelpRow> Compare =
    [
        new("Select Left", "←", Intent.VoteLeft),
        new("Select Right", "→", Intent.VoteRight),
        new("Discard", "1 / 2", Intent.DiscardLeft), // one row covers both discard keys, as the old app did
        new("Special", "4 / 5", Intent.SpecialLeft), // one row covers both special keys
        new("Open", "O", Intent.OpenFolder),
        new("Undo last action", "Ctrl+Z", Intent.Undo),
        new("Save", "Ctrl+S", Intent.Save),
        new("Help", "F1", Intent.ToggleHelp),
        new("Exit", "Esc", Intent.Quit),
    ];
}
