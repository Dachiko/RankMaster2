using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// The owner's report: click to vote, the app hangs, another click and it silently closes. The
/// crash log named it exactly: <c>MatchStrip.Render</c> looked its ball colours up through
/// <c>Application.Current.FindResource</c>, but Theme.axaml (StripUpset/StripConfirmation) is
/// merged into UiRoot's own Resources (Ui/Views/UiRoot.axaml), not the Application's --
/// Application-level lookup returned AvaloniaProperty.UnsetValue, and the cast to IBrush threw the
/// moment a session's first cue had to be drawn. Nothing exercised this path before: every other
/// UiRoot-based test opens a folder with no votes cast, so Cues was always empty and Render's early
/// return (`cues.Count == _lastCount`) never reached the resource lookup at all.
/// </summary>
public class MatchStripTests
{
    [AvaloniaFact]
    public async Task A_session_with_cues_renders_the_strip_without_crashing()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(cues: ["confirmation", "upset"]) };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var coordinator = new RankCoordinator(link, stills, null, clock, new SynchronousUiThread(), new ImmediateDelay(),
            new LastFolderStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rm2-matchstrip-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        var root = new UiRoot(coordinator);
        var window = new Window { Content = root, Width = 1280, Height = 720 };
        window.Show();

        // Any exception MatchStrip.Render throws surfaces here: UiRoot renders inline on the UI
        // thread in response to the snapshot OpenFolderAsync applies.
        Assert.True(await coordinator.OpenFolderAsync("/lib"));
        Dispatcher.UIThread.RunJobs(); // let the layout pass attach RankView/MatchStrip to the visual tree

        var strip = FindDescendant<MatchStrip>(root)!;
        Assert.True(strip.IsVisible);

        var balls = (StackPanel)typeof(MatchStrip).GetField("_balls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(strip)!;
        Assert.Equal(2, balls.Children.Count);
        foreach (var child in balls.Children)
        {
            var fill = Assert.IsType<Ellipse>(child).Fill;
            Assert.IsAssignableFrom<IBrush>(fill); // not AvaloniaProperty.UnsetValue
        }
    }

    private static T? FindDescendant<T>(Control root) where T : Control
    {
        if (root is T match) return match;
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control c)
            {
                var found = FindDescendant<T>(c);
                if (found is not null) return found;
            }
        }
        return null;
    }
}
