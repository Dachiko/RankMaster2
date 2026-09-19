using System.Linq;
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

    /// <summary>
    /// The strip caps at 10 balls (SPEC.md). Past a session's tenth vote, Count never changes
    /// again -- comparing only Count (as Render used to) meant every vote past the tenth silently
    /// stopped updating the balls, even though which ones are confirmations vs upsets keeps shifting
    /// as the oldest one drops off the front. Drives Render directly (the bug is entirely inside it);
    /// the surrounding UiRoot/window is only there so `this.FindResource` has Theme.axaml to find.
    /// </summary>
    [AvaloniaFact]
    public async Task Render_updates_the_balls_when_content_changes_but_the_ten_cap_does_not()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking() };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var coordinator = new RankCoordinator(link, stills, null, clock, new SynchronousUiThread(), new ImmediateDelay(),
            new LastFolderStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rm2-matchstrip-cap-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        var root = new UiRoot(coordinator);
        var window = new Window { Content = root, Width = 1280, Height = 720 };
        window.Show();

        Assert.True(await coordinator.OpenFolderAsync("/lib"));
        Dispatcher.UIThread.RunJobs();
        var strip = FindDescendant<MatchStrip>(root)!;
        var balls = (StackPanel)typeof(MatchStrip).GetField("_balls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(strip)!;

        strip.Render(Enumerable.Repeat("confirmation", 10).ToArray());
        Assert.Equal(10, balls.Children.Count);
        var lastFillBefore = ((Ellipse)balls.Children[9]).Fill;

        strip.Render(Enumerable.Repeat("confirmation", 9).Append("upset").ToArray());
        Assert.Equal(10, balls.Children.Count); // the cap itself never changes
        var lastFillAfter = ((Ellipse)balls.Children[9]).Fill;
        Assert.NotSame(lastFillBefore, lastFillAfter); // but which ball is which colour must
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
