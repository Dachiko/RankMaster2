using System.Linq;
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

    // A snapshot of the last cues actually drawn, so a real content change can be told from a
    // no-op repaint. The strip caps at 10 (SPEC.md), so once a session has 10 votes its Count never
    // changes again -- comparing only Count (as this used to) meant every vote past the tenth
    // silently stopped updating the balls, even though which ones are confirmations vs upsets keeps
    // shifting as the oldest one drops off the front.
    private IReadOnlyList<string> _lastCues = [];

    public MatchStrip()
    {
        AvaloniaXamlLoader.Load(this);
        _balls = this.FindControl<StackPanel>("Balls")!;
    }

    public void Render(IReadOnlyList<string> cues)
    {
        IsVisible = cues.Count > 0;
        if (cues.SequenceEqual(_lastCues)) return;

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
        _lastCues = cues.ToArray(); // snapshot: the caller's list may be reused/mutated after this returns
    }
}
