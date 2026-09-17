using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RankMaster2.Pc.App;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Video;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// The compare screen (plan § 4.2): two <see cref="PaneControl"/>s plus every overlay of § 1.2, in
/// z-order panes → filenames (drawn inside each pane) → info card → action bar → match strip →
/// toast → late-action line → help sheet. Decides nothing: every value comes from
/// <see cref="RankCoordinator.Rank"/>, re-read on every <see cref="Refresh"/>.
/// </summary>
public partial class RankView : UserControl, IRefreshable
{
    private readonly RankCoordinator _coordinator;
    private readonly PaneControl _left;
    private readonly PaneControl _right;
    private readonly InfoCard _card;
    private readonly ActionBar _actions;
    private readonly Border _helpButton;
    private readonly HelpSheet _help;
    private readonly MatchStrip _strip;
    private readonly Toast _toast;
    private readonly LateActionLine _lateLine;

    private Side? _lastCueSide;
    private bool _cuePlaying;
    private DispatcherTimer? _cursorTimer;
    private int _panesPaintedOpenSequence = -1;

    public RankView(RankCoordinator coordinator)
    {
        AvaloniaXamlLoader.Load(this);
        _coordinator = coordinator;

        _left = this.FindControl<PaneControl>("LeftPane")!;
        _right = this.FindControl<PaneControl>("RightPane")!;
        _card = this.FindControl<InfoCard>("Card")!;
        _actions = this.FindControl<ActionBar>("Actions")!;
        _helpButton = this.FindControl<Border>("HelpButton")!;
        _help = this.FindControl<HelpSheet>("Help")!;
        _strip = this.FindControl<MatchStrip>("Strip")!;
        _toast = this.FindControl<Toast>("ToastControl")!;
        _lateLine = this.FindControl<LateActionLine>("LateLine")!;

        _left.PointerPressed += (_, _) => _ = _coordinator.OnPaneClicked(Side.Left);
        _right.PointerPressed += (_, _) => _ = _coordinator.OnPaneClicked(Side.Right);
        _actions.ActionClicked += intent => _ = _coordinator.OnActionButtonClicked(intent);

        _helpButton.PointerEntered += (_, _) => _help.SetButtonHover(true);
        _helpButton.PointerExited += (_, _) => _help.SetButtonHover(false);

        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "";
        _help.SetVersion(version);

        PointerMoved += (_, _) => OnPointerActivity();
        Loaded += (_, _) => { Focus(); UpdatePaneSize(); };
        _left.SizeChanged += (_, _) => UpdatePaneSize();
    }

    // ---- pane size (H10): stills and video decode at the size they are actually shown at ---------

    /// <summary>
    /// Both panes are always the same size (<see cref="Ui.Views.UiRootTests"/>'s own
    /// "Pane_widths_are_equal_and_full_height"), so the left pane's bounds stand for "the" pane size.
    /// Multiplied by the window's render scaling because <see cref="IStillSource.SetPaneSize"/> and
    /// <see cref="IVideoSurface.SetPaneSize"/> both want physical device pixels, not DIPs.
    /// </summary>
    private void UpdatePaneSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = (int)Math.Round(_left.Bounds.Width * scaling);
        var height = (int)Math.Round(_left.Bounds.Height * scaling);
        if (width < 1 || height < 1) return; // not laid out yet

        _coordinator.SetPaneSize(width, height);

        var pixels = new PixelSize(width, height);
        _coordinator.LeftVideoSurface?.SetPaneSize(pixels);
        _coordinator.RightVideoSurface?.SetPaneSize(pixels);
    }

    public void Refresh()
    {
        var rank = _coordinator.Rank;

        if (rank.Snapshot is { } snapshot)
        {
            _card.Render(snapshot);
            _strip.Render(rank.Strip);
        }

        if (rank.Left is { } left)
            _left.Render(left, Side.Left, _coordinator.LeftVideoSurface, _coordinator.VideoEngineStatus, Clock);
        if (rank.Right is { } right)
            _right.Render(right, Side.Right, _coordinator.RightVideoSurface, _coordinator.VideoEngineStatus, Clock);

        _help.SetPinned(rank.HelpPinned);
        _toast.Render(rank.Toast, Clock.UtcNow);
        _lateLine.Render(rank.Busy, rank.InFlightSince, Clock);
        MaybeMarkPanesPainted(rank);

        if (rank.CueSide != _lastCueSide)
        {
            _lastCueSide = rank.CueSide;
            if (rank.CueSide is { } side && !_cuePlaying)
            {
                _cuePlaying = true;
                var pane = side == Side.Left ? _left : _right;
                _ = pane.PlayCue().ContinueWith(_ => _cuePlaying = false, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
    }

    /// <summary>Real time in production; a test sets a <see cref="FakeClock"/> to check the 300 ms
    /// still-ring grace, the 300 ms late-action line and the 2.5 s toast without a real wait.</summary>
    public IClock Clock { get; set; } = SystemClock.Instance;

    /// <summary>A22: marks the startup kit's <c>panes_painted</c> point (plan A-startup-and-shell.md
    /// § 6.5) the first time both panes hold pixels for a given open of <see cref="RankCoordinator.OpenSequence"/>
    /// -- once per folder open, even though this <see cref="RankView"/> instance outlives any one
    /// open (UiRoot caches it).</summary>
    private void MaybeMarkPanesPainted(RankModel rank)
    {
        if (_panesPaintedOpenSequence == _coordinator.OpenSequence) return;
        if (rank.Left is not { Kind: PaneKind.Ready or PaneKind.Refining }) return;
        if (rank.Right is not { Kind: PaneKind.Ready or PaneKind.Refining }) return;

        _panesPaintedOpenSequence = _coordinator.OpenSequence;
        StartupClock.Mark("panes_painted");
    }

    // ---- cursor hiding (plan § 1, compare screen only) -----------------------------------------

    private void OnPointerActivity()
    {
        Cursor = Cursor.Default;
        _cursorTimer ??= new DispatcherTimer { Interval = Timings.CursorHideAfterMs };
        _cursorTimer.Stop();
        _cursorTimer.Tick -= HideCursor;
        _cursorTimer.Tick += HideCursor;
        _cursorTimer.Start();
    }

    private void HideCursor(object? sender, EventArgs e)
    {
        _cursorTimer?.Stop();
        Cursor = new Cursor(StandardCursorType.None);
    }
}
