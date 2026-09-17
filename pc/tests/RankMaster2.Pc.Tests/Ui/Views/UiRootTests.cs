using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// Plan § 6.1's headless suite: key routing at the <c>TopLevel</c>, regardless of focus; Space and
/// Enter doing nothing on the compare screen; the help sheet toggling; <c>Esc</c> still quitting
/// with the sheet open; equal pane widths at three window sizes.
/// </summary>
public class UiRootTests
{
    private static (Window Window, UiRoot Root, RankCoordinator Coordinator, FakeSessionLink Link, FakeClock Clock, FakeStillSource Stills) Build()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking() };
        var stills = new FakeStillSource();
        var clock = new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var coordinator = new RankCoordinator(link, stills, null, clock, new SynchronousUiThread(), new ImmediateDelay(),
            new LastFolderStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rm2-ui-view-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));
        var root = new UiRoot(coordinator);
        var window = new Window { Content = root, Width = 1280, Height = 720 };
        window.Show();
        return (window, root, coordinator, link, clock, stills);
    }

    [AvaloniaFact]
    public async Task Right_arrow_reaches_the_model_even_with_nothing_focused()
    {
        var (window, _, coordinator, link, clock, _) = Build();
        await coordinator.OpenFolderAsync("/lib");
        coordinator.Rank.SetPanes(coordinator.Rank.Left! with { Kind = PaneKind.Ready }, coordinator.Rank.Right! with { Kind = PaneKind.Ready }, coordinator.Rank.PairArrivedAt);
        clock.Advance(Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1));

        window.KeyPress(Key.Right, RawInputModifiers.None);
        await Task.Delay(50);

        // Nothing on this screen is focusable (plan § 3.7), so the arrow key had nowhere to go
        // except the tunnel handler -- the vote call is the proof it was routed there.
        Assert.Equal(1, link.CallCount(nameof(ISessionLink.VoteAsync)));
    }

    [AvaloniaFact]
    public void F1_toggles_the_help_sheet_and_Esc_still_quits_with_it_open()
    {
        var (window, _, coordinator, _, _, _) = Build();
        _ = coordinator.OpenFolderAsync("/lib");

        Assert.False(coordinator.Rank.HelpPinned);
        window.KeyPress(Key.F1, RawInputModifiers.None);
        Assert.True(coordinator.Rank.HelpPinned);

        var quit = false;
        coordinator.QuitRequested += () => quit = true;
        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Assert.True(quit);
    }

    [AvaloniaFact]
    public void Space_and_Enter_do_nothing_on_the_compare_screen()
    {
        var (window, _, coordinator, link, _, _) = Build();
        _ = coordinator.OpenFolderAsync("/lib");

        var votesBefore = link.CallCount(nameof(ISessionLink.VoteAsync));
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        window.KeyPress(Key.Space, RawInputModifiers.None);

        Assert.Equal(votesBefore, link.CallCount(nameof(ISessionLink.VoteAsync)));
        Assert.False(coordinator.Rank.Busy);
    }

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(800, 600)]
    public void Pane_widths_are_equal_and_full_height(int width, int height)
    {
        var (window, root, coordinator, _, _, _) = Build();
        _ = coordinator.OpenFolderAsync("/lib");
        window.Width = width;
        window.Height = height;
        Dispatcher.UIThread.RunJobs();

        var rankView = FindDescendant<RankView>(root)!;
        var panes = FindDescendant<Grid>(rankView, "Panes")!;
        var left = FindDescendant<PaneControl>(panes, "LeftPane")!;
        var right = FindDescendant<PaneControl>(panes, "RightPane")!;

        Assert.Equal(left.Bounds.Width, right.Bounds.Width, 0.5);
        Assert.Equal(panes.Bounds.Height, left.Bounds.Height, 0.5);
        Assert.Equal(panes.Bounds.Height, right.Bounds.Height, 0.5);
    }

    /// <summary>H10: stills decoded at the constructor default (960x1080) regardless of the real
    /// window because nothing called <see cref="RankCoordinator.SetPaneSize"/>. Now RankView reports
    /// the laid-out pane size on Loaded/SizeChanged.</summary>
    [AvaloniaFact]
    public void Opening_a_folder_forwards_the_laid_out_pane_size_to_the_still_source()
    {
        var (_, _, coordinator, _, _, stills) = Build();
        _ = coordinator.OpenFolderAsync("/lib");
        Dispatcher.UIThread.RunJobs();

        Assert.True(stills.PaneWidth > 0);
        Assert.True(stills.PaneHeight > 0);
        Assert.NotEqual(960, stills.PaneWidth);
        Assert.NotEqual(1080, stills.PaneHeight);
    }

    /// <summary>§ 3.13: on the rename screen, Esc is wired to the coordinator's own cancel path, not
    /// <see cref="RankCoordinator.QuitRequested"/> -- "Esc on this screen cancels the rename, not
    /// the program."</summary>
    [AvaloniaFact]
    public void Esc_on_the_rename_confirmation_screen_returns_to_start_and_does_not_quit()
    {
        var (window, _, coordinator, link, _, _) = Build();
        coordinator.BeginRenameConfirm("/lib/photos");
        Assert.Equal(AppScreen.Rename, coordinator.Screen);

        var quit = false;
        coordinator.QuitRequested += () => quit = true;
        window.KeyPress(Key.Escape, RawInputModifiers.None);

        Assert.False(quit);
        Assert.Equal(AppScreen.Start, coordinator.Screen);
        Assert.Empty(link.Calls);
    }

    private static T? FindDescendant<T>(Control root, string? name = null) where T : Control
    {
        if (root is T match && (name is null || root.Name == name)) return match;
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control c)
            {
                var found = FindDescendant<T>(c, name);
                if (found is not null) return found;
            }
        }
        return null;
    }
}
