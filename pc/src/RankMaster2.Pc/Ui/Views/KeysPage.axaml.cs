using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// Plan H § 3.3: the start screen's F1 page. Three columns (<c>01 RANK</c>, <c>02 START</c>,
/// <c>03 EVERYWHERE</c>) of key chips and what they do, read from <see cref="HelpRows.StartPage"/> so
/// the page and <see cref="KeyMap"/> cannot drift apart; the version in the corner; "ANY KEY CLOSES"
/// at the bottom. The compare screen keeps <see cref="HelpSheet"/> (plan H § 2.2.4), which this does
/// not replace. Whether the page is open is the coordinator's (<c>Rank.HelpPinned</c>, the same flag
/// F1 has always toggled); this control only shows it.
/// </summary>
public partial class KeysPage : UserControl
{
    /// <summary>A click anywhere on the page: it is closed like a key would close it.</summary>
    public event Action? CloseRequested;

    public KeysPage()
    {
        AvaloniaXamlLoader.Load(this);
        _fade = new FadeHost(this);
        this.FindControl<ItemsControl>("Columns")!.ItemsSource = HelpRows.StartPage;
        this.FindControl<TextBlock>("VersionText")!.Text = "v" + AppInfo.Version;
        PointerPressed += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        };
    }

    private readonly FadeHost _fade;

    /// <summary>Plan I: the folder browser shows this same page with its own three columns
    /// (<see cref="HelpRows.BrowsePage"/>).</summary>
    public void SetSections(IReadOnlyList<HelpSection> sections) =>
        this.FindControl<ItemsControl>("Columns")!.ItemsSource = sections;

    /// <summary>What was last asked for. The page's own <c>IsVisible</c> lags a close by the fade
    /// (plan H § 5 S3: it fades in and out over ~150 ms).</summary>
    public bool IsOpen => _fade.IsShown;

    public void SetOpen(bool open)
    {
        if (open) _fade.Show();
        else _fade.Hide();
    }
}
