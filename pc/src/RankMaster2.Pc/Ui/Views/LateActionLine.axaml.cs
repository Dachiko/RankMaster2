using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// Plan § 1.2: a 2px indeterminate line along the top edge, appearing only when an action has been
/// in flight for more than 300 ms. No text, no layout reservation, and it cannot be confused with
/// the info card's progress bar.
/// </summary>
public partial class LateActionLine : UserControl
{
    public LateActionLine() => AvaloniaXamlLoader.Load(this);

    public void Render(bool busy, DateTimeOffset? inFlightSince, IClock clock) =>
        IsVisible = busy && inFlightSince is { } since && clock.UtcNow - since >= Timings.LateActionLineAfterMs;
}
