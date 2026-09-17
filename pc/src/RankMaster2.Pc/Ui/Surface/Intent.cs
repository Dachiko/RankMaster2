namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;

/// <summary>
/// The ten intents left on the PC client's surface after the owner's ruling removed the skip
/// action (2026-09-17, "I really don't use skip" -- the phone already had none): everything a key or a
/// click can mean on either screen. <see cref="None"/> is "swallow the key, do nothing" -- the
/// compare screen's catch-all so Avalonia's own key handling never runs (plan § 3.7).
/// </summary>
public enum Intent
{
    None = 0,
    VoteLeft,
    VoteRight,
    DiscardLeft,
    DiscardRight,
    SpecialLeft,
    SpecialRight,
    Undo,
    Save,
    OpenFolder,
    ToggleHelp,
    Quit,
}

/// <summary>Small helpers so callers do not repeat the side-to-intent mapping by hand.</summary>
public static class Intents
{
    public static Intent Vote(Side side) => side == Side.Left ? Intent.VoteLeft : Intent.VoteRight;
    public static Intent Discard(Side side) => side == Side.Left ? Intent.DiscardLeft : Intent.DiscardRight;
    public static Intent Special(Side side) => side == Side.Left ? Intent.SpecialLeft : Intent.SpecialRight;

    /// <summary>True for the six intents that name one particular pair action (the ones the gate
    /// and the pane-readiness rule apply to). False for Undo, Save, OpenFolder, ToggleHelp, Quit, None.</summary>
    public static bool IsPairAction(this Intent intent) => intent is
        Intent.VoteLeft or Intent.VoteRight or
        Intent.DiscardLeft or Intent.DiscardRight or Intent.SpecialLeft or Intent.SpecialRight;

    /// <summary>Undo and Save: gated on busy and key-release only (plan § 1.1 "busy only"; § 3.1
    /// rules 2, 4 and 5 do not apply to them — neither carries a pairSeq on the wire).</summary>
    public static bool IsBusyOnlyAction(this Intent intent) => intent is Intent.Undo or Intent.Save;

    /// <summary>The side a pair action is "about", for pane-readiness (plan § 3.2). Null for
    /// Undo, Save and the non-gated intents.</summary>
    public static Side? Subject(this Intent intent) => intent switch
    {
        Intent.VoteLeft or Intent.DiscardLeft or Intent.SpecialLeft => Side.Left,
        Intent.VoteRight or Intent.DiscardRight or Intent.SpecialRight => Side.Right,
        _ => null,
    };

    public static bool IsVote(this Intent intent) => intent is Intent.VoteLeft or Intent.VoteRight;
    public static bool IsDiscard(this Intent intent) => intent is Intent.DiscardLeft or Intent.DiscardRight;
    public static bool IsSpecial(this Intent intent) => intent is Intent.SpecialLeft or Intent.SpecialRight;
}
