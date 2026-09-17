using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// Plan § 3.8: an overlay in the view's own visual tree, not an Avalonia <c>Popup</c> -- the old
/// app's re-layout bug lived exactly there. Shown by hover on the <c>?</c> or the sheet itself,
/// hidden when the pointer leaves both unless <c>F1</c> pinned it. Its rows are
/// <see cref="HelpRows"/>, so a test can check them against <see cref="KeyMap"/> without the two
/// drifting apart (plan § 3.8's last sentence).
/// </summary>
public partial class HelpSheet : UserControl
{
    private bool _pinned;
    private bool _hoveringButton;
    private bool _hoveringSheet;

    public bool Pinned => _pinned;

    public HelpSheet()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ItemsControl>("Rows")!.ItemsSource = HelpRows.Compare;
        PointerEntered += (_, _) => { _hoveringSheet = true; UpdateVisibility(); };
        PointerExited += (_, _) => { _hoveringSheet = false; UpdateVisibility(); };
    }

    public void SetVersion(string version) => this.FindControl<TextBlock>("VersionText")!.Text = version;

    public void SetButtonHover(bool hovering)
    {
        _hoveringButton = hovering;
        UpdateVisibility();
    }

    /// <summary><c>F1</c>: toggles the pin. Does not itself decide whether the key was pressed --
    /// that is the coordinator's <c>ToggleHelp</c> intent; this just reflects it.</summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        UpdateVisibility();
    }

    private void UpdateVisibility() => IsVisible = _pinned || _hoveringButton || _hoveringSheet;
}
