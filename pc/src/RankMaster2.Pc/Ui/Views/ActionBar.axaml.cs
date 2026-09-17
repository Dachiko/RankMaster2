using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>Plan § 1.2: "a mouse route for four actions, and -- more useful -- the on-screen
/// reminder of the four keys that are not intuitive." Every button is <c>Focusable="False"</c> so
/// none of them can ever take an arrow key (plan § 3.7).</summary>
public partial class ActionBar : UserControl
{
    public event Action<Intent>? ActionClicked;

    public ActionBar()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("DiscardLeftButton")!.Click += (_, _) => ActionClicked?.Invoke(Intent.DiscardLeft);
        this.FindControl<Button>("SpecialLeftButton")!.Click += (_, _) => ActionClicked?.Invoke(Intent.SpecialLeft);
        this.FindControl<Button>("SpecialRightButton")!.Click += (_, _) => ActionClicked?.Invoke(Intent.SpecialRight);
        this.FindControl<Button>("DiscardRightButton")!.Click += (_, _) => ActionClicked?.Invoke(Intent.DiscardRight);
    }
}
