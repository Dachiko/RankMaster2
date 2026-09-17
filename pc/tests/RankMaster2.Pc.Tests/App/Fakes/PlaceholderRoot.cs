namespace RankMaster2.Pc.App.Seams;

// MOVED HERE AT INTEGRATION. This was the shell's stand-in root while part E was still being
// built. The real UiRoot has replaced it in production, but part A's shell tests still use it as a
// plain Control to check the window's shape without dragging the whole surface in - which is a
// perfectly good test double. It keeps its old namespace so those tests read unchanged.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

/// <summary>
/// TEMPORARY SCAFFOLD. Part E's <c>Ui/UiRoot</c> (E-ranking-surface.md § 2.1-2.2) had not landed in
/// this worktree when part A was built. A-startup-and-shell.md § 9 (Phase A1) allows exactly this:
/// "the shell shows black full screen with a placeholder TextBlock ... otherwise A1 stubs [the seam
/// types] under App/Seams/ temporarily and deletes the stubs when the real ones land." This is that
/// stub — its only contract is <see cref="QuitRequested"/>, which is <c>UiRoot</c>'s (E § 2.2).
///
/// Delete this file and <see cref="Composition"/>'s reference to it, and give <c>Composition</c> the
/// real <c>UiRoot</c> in its place, the day <c>Ui/</c> exists. Nothing else in <c>App/</c> depends on
/// this type — <c>MainWindow</c> only ever sees <see cref="Control"/> and the event.
/// </summary>
public sealed class PlaceholderRoot : UserControl
{
    public event EventHandler? QuitRequested;

    public PlaceholderRoot(string version)
    {
        Background = Brushes.Black;
        Focusable = true;
        Content = new TextBlock
        {
            Text = $"Rank Master 2 — {version}",
            Foreground = Brushes.White,
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            QuitRequested?.Invoke(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }
}
