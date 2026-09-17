using Avalonia;
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
                Fill = cue == "upset"
                    ? (IBrush)Application.Current!.FindResource("StripUpset")!
                    : (IBrush)Application.Current!.FindResource("StripConfirmation")!,
            });
        }
        _lastCount = cues.Count;
    }
}
