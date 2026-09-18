using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// Plan § 1.2, SPEC.md § Match strip: up to 10 balls, emerald (confirmation) or amber (upset),
/// session-only, hidden until the first vote. Cues arrive oldest first; the strip draws them left
/// to right in that order, so the newest ball is always the rightmost one.
/// </summary>
public partial class MatchStrip : UserControl
{
    private readonly StackPanel _balls;
    private int _lastCount;

    public MatchStrip()
    {
        AvaloniaXamlLoader.Load(this);
        _balls = this.FindControl<StackPanel>("Balls")!;
    }

    public void Render(IReadOnlyList<string> cues)
    {
        IsVisible = cues.Count > 0;
        if (cues.Count == _lastCount && cues.Count == _balls.Children.Count) return;

        _balls.Children.Clear();
        foreach (var cue in cues)
        {
            _balls.Children.Add(new Ellipse
            {
                Width = 12,
                Height = 12,
                // Theme.axaml is merged into UiRoot's own Resources (Ui/Views/UiRoot.axaml), not the
                // Application's -- Application.Current.FindResource looks only at the latter and
                // returns AvaloniaProperty.UnsetValue for a key it never has, crashing this cast the
                // moment the strip first has a cue to draw. this.FindResource walks the logical tree
                // from this control upward, the same resolution {DynamicResource ...} bindings use in
                // XAML, and reaches UiRoot's merged dictionary correctly.
                Fill = cue == "upset"
                    ? (IBrush)this.FindResource("StripUpset")!
                    : (IBrush)this.FindResource("StripConfirmation")!,
            });
        }
        _lastCount = cues.Count;
    }
}
