namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// The help sheet's rows (plan § 3.8), in the old app's order, as plain data so
/// <c>Views/HelpSheet</c> renders them and a headless test can check them against
/// <see cref="KeyMap"/> without the two drifting apart.
/// </summary>
public sealed record HelpRow(string Label, string Keys, Intent Intent)
{
    /// <summary>The caption split into one key per chip (plan H § 3.3: "each key an ink-outlined
    /// chip"): "Ctrl+Z" is two chips, "1 / 2" is two chips, "←" is one. Only the keys page uses it.</summary>
    public IReadOnlyList<string> Chips { get; } = Keys
        .Split([" / ", "+"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>One column of the start screen's keys page (plan H § 3.3): a red two-digit number, an
/// ink caps heading, its rows.</summary>
public sealed record HelpSection(string Number, string Heading, IReadOnlyList<HelpRow> Rows);

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

    /// <summary>
    /// The start screen's own keys (plan H § 3.3's column "02 Start", § 3.4's table). Every row maps
    /// through <see cref="KeyMap.MapStart"/>, including the two that map to <see cref="Intent.None"/>
    /// on purpose: <c>Enter</c> and <c>← / →</c> are the focused pill's business (Avalonia's own button
    /// behaviour and the view's focus move), not KeyMap's (see <see cref="KeyMap.MapStart"/>).
    /// </summary>
    public static readonly IReadOnlyList<HelpRow> Start =
    [
        new("Resume", "Enter", Intent.None),
        new("Move", "← / →", Intent.None),
        new("Open folder", "O", Intent.OpenFolder),
        new("Rename by rank", "R", Intent.RenameFolder),
        new("Take back last pair", "Ctrl+Z", Intent.Undo),
    ];

    /// <summary>Column "03 Everywhere": the two keys that mean the same on every screen.</summary>
    public static readonly IReadOnlyList<HelpRow> Everywhere =
    [
        new("Keys", "F1", Intent.ToggleHelp),
        new("Close · quit", "Esc", Intent.Quit),
    ];

    /// <summary>Column "01 Rank": the compare screen's pair keys, taken from <see cref="Compare"/> so
    /// the two lists cannot disagree. Open, Help and Exit are left out here because they are in the
    /// other two columns.</summary>
    public static readonly IReadOnlyList<HelpRow> Rank = Compare
        .Where(r => r.Intent.IsPairAction() || r.Intent is Intent.Undo or Intent.Save)
        .ToList();

    /// <summary>The three columns of the start screen's keys page, in order.</summary>
    public static readonly IReadOnlyList<HelpSection> StartPage =
    [
        new("01", "RANK", Rank),
        new("02", "START", Start),
        new("03", "EVERYWHERE", Everywhere),
    ];

    // ---- the folder browser's keys page (plan I § 2.2 item 5, same page as plan H § 3.3) ------------------------
    // Each row's caption names the key MapBrowse maps to the row's Intent; the one typing row is None on purpose
    // (letters arrive as text, not as a key).

    public static readonly IReadOnlyList<HelpRow> BrowseMove =
    [
        new("Move", "↑ / ↓", Intent.BrowseUp),
        new("Page", "PgUp / PgDn", Intent.BrowsePageUp),
        new("First · last", "Home / End", Intent.BrowseHome),
    ];

    public static readonly IReadOnlyList<HelpRow> BrowseFolders =
    [
        new("Go into", "→", Intent.BrowseInto),
        new("Go up", "←", Intent.BrowseOut),
        new("Choose this folder", "Enter", Intent.BrowseEnter),
    ];

    public static readonly IReadOnlyList<HelpRow> BrowseFind =
    [
        new("Find by typing", "A–Z", Intent.None),
        new("Delete a letter · go up", "Backspace", Intent.BrowseBackspace),
        new("Clear letters · leave", "Esc", Intent.BrowseLeave),
        new("Keys", "F1", Intent.ToggleHelp),
    ];

    /// <summary>The three columns of the browser's keys page.</summary>
    public static readonly IReadOnlyList<HelpSection> BrowsePage =
    [
        new("01", "MOVE", BrowseMove),
        new("02", "FOLDERS", BrowseFolders),
        new("03", "FIND", BrowseFind),
    ];
}
