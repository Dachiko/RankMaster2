namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// (key, modifiers, screen) → <see cref="Intent"/>. Plan § 1.1, verbatim. A pure function: no
/// state, no Avalonia type. `Shift`/`Alt` are ignored everywhere except that they cannot turn a key
/// into something it is not (they are simply not looked at). `Ctrl` is looked at for exactly two
/// keys, `Z` and `S`, because those are the two keys whose meaning it changes; every other key is
/// mapped the same whether or not Ctrl is down (so a Ctrl+&lt;arrow&gt; still votes — nothing in
/// SPEC.md or the plan asks for it to do otherwise, and swallowing it would be inventing a rule).
/// </summary>
public static class KeyMap
{
    /// <summary>The compare screen's map (plan § 1.1's first table).</summary>
    public static Intent MapCompare(UiKey key, UiModifiers modifiers)
    {
        var ctrl = (modifiers & UiModifiers.Control) != 0;

        // Ctrl changes the meaning of exactly this one key on the compare screen; check it first so
        // Ctrl+S can never fall through to something else and Ctrl+Z can never fall through to nothing.
        if (key == UiKey.S && ctrl) return Intent.Save;
        if (key == UiKey.Z) return ctrl ? Intent.Undo : Intent.None;

        return key switch
        {
            UiKey.Left => Intent.VoteLeft,
            UiKey.Right => Intent.VoteRight,
            // Down and S alone used to skip the pair; removed from the PC client's surface by the
            // owner's ruling (2026-09-17, "I really don't use skip") -- SPEC.md § Keys, the phone
            // already dropped it. The server keeps POST /session/skip and every test of it; nothing
            // here calls it.
            UiKey.D1 or UiKey.NumPad1 => Intent.DiscardLeft,
            UiKey.D2 or UiKey.NumPad2 => Intent.DiscardRight,
            UiKey.D4 or UiKey.NumPad4 => Intent.SpecialLeft,
            UiKey.D5 or UiKey.NumPad5 => Intent.SpecialRight,
            UiKey.O => Intent.OpenFolder,
            UiKey.F1 => Intent.ToggleHelp,
            UiKey.Escape => Intent.Quit,
            _ => Intent.None,
        };
    }

    /// <summary>
    /// The start screen's map (plan § 1.1 second paragraph): `O`, `F1`, `Esc`, and `Ctrl+Z` — the
    /// last one only meaningful while a session is open and exhausted, which is <see cref="StartModel"/>'s
    /// job to gate, not this function's (KeyMap only says what the key *could* mean on this screen).
    /// `Enter`/`Space` are deliberately not here: they reach the focused button through Avalonia's own
    /// behaviour (plan § 3.7), never through KeyMap.
    /// </summary>
    public static Intent MapStart(UiKey key, UiModifiers modifiers)
    {
        var ctrl = (modifiers & UiModifiers.Control) != 0;

        if (key == UiKey.Z) return ctrl ? Intent.Undo : Intent.None;

        return key switch
        {
            UiKey.O => Intent.OpenFolder,
            UiKey.F1 => Intent.ToggleHelp,
            UiKey.Escape => Intent.Quit,
            _ => Intent.None,
        };
    }
}
