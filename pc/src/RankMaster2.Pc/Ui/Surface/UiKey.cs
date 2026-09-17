namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// An abstract physical key, decoupled from any windowing toolkit's key enum. <see cref="KeyMap"/>
/// and everything downstream of it is a pure function over this type, so it compiles and tests with
/// no Avalonia reference (pc/plans/E-ranking-surface.md § 2.1). Views/ maps Avalonia's
/// <c>Avalonia.Input.Key</c> onto this one at the single point key events enter the program.
///
/// Only the keys SPEC.md § Keys and this plan's § 1.1 name are represented; everything else maps to
/// <see cref="None"/> in Views/ before it ever reaches <see cref="KeyMap"/>.
/// </summary>
public enum UiKey
{
    None = 0,
    Left, Right, Down, Up,
    S, Z, O,
    D1, D2, D3, D4, D5,
    NumPad1, NumPad2, NumPad3, NumPad4, NumPad5,
    F1,
    Escape,
    Enter,
    Space,
}

/// <summary>Modifier keys held alongside <see cref="UiKey"/>. Either physical Ctrl key is one flag.</summary>
[Flags]
public enum UiModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}
