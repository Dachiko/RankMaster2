using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// What part A hosts as window content (plan § 2.2). Switches between <see cref="StartView"/>, <see cref="BrowseView"/> (plan I) and
/// <see cref="RankView"/> as <see cref="RankCoordinator.Screen"/> changes, attaches the key handlers
/// to the <see cref="TopLevel"/> in the tunnel phase (plan § 2.2, § 3.7), and exposes
/// <see cref="QuitRequested"/>. Decides nothing itself -- every decision is <c>Surface/</c>'s.
/// </summary>
public partial class UiRoot : UserControl, IUiThread
{
    private readonly RankCoordinator? _coordinator;
    private StartView? _startView;
    private RankView? _rankView;
    private RenameView? _renameView;
    private BrowseView? _browseView;
    private Grid? _startStack;
    private HouseMotion? _motion;
    private AppScreen? _renderedScreen;
    private DispatcherTimer? _repaintTimer;

    /// <summary>Raised by <c>Esc</c> (plan § 2.2). A fires <c>DELETE /session</c> with a short
    /// timeout and exits; this control does not touch process lifetime.</summary>
    public event Action? QuitRequested;

    /// <summary>Designer/XAML-loader constructor. Not for production use -- <see cref="Rebuild"/>
    /// tolerates a null coordinator so the XAML previewer does not crash.</summary>
    public UiRoot()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public UiRoot(RankCoordinator coordinator) : this()
    {
        _coordinator = coordinator;
        _motion = new HouseMotion(this.FindControl<DecodeLayer>("Decode")!, new UserActivity());
        _coordinator.Changed += OnCoordinatorChanged;
        _coordinator.QuitRequested += () => QuitRequested?.Invoke();

        // A20: nothing else repaints on a clock -- Render() only runs from Changed, which nothing
        // raises while a toast is quietly expiring or the late-action line's 300 ms is elapsing.
        // Tick() itself decides whether anything actually changed.
        _repaintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _repaintTimer.Tick += (_, _) => _coordinator.Tick();
        _repaintTimer.Start();

        Render();
    }

    // ---- IUiThread: the real one, for RankCoordinator's use in production -------------------------

    void IUiThread.Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ---- screen switching ---------------------------------------------------------------------

    private void OnCoordinatorChanged() => ((IUiThread)this).Post(Render);

    private void Render()
    {
        if (_coordinator is null) return;
        var root = this.FindControl<ContentControl>("Root");
        if (root is null) return;

        // Plan H § 3.2: the start screen and the rename card share one stack, so the start screen stays
        // drawn under the card's veil during AppScreen.Rename. A control can have only one visual parent,
        // which is why the start view lives in the stack for both screens instead of being swapped in and
        // out of the ContentControl.
        if (_renderedScreen != _coordinator.Screen)
        {
            _renderedScreen = _coordinator.Screen;
            root.Content = _coordinator.Screen switch
            {
                AppScreen.Rank => _rankView ??= new RankView(_coordinator),
                // Plan I: the folder browser replaces the screen it was opened from; both come back as they were
                // (the cached views keep their state, and the compare screen's session was never touched).
                AppScreen.Browse => _browseView ??= new BrowseView(_coordinator, _motion!),
                _ => StartStack(),
            };
        }

        if (_coordinator.Screen == AppScreen.Browse)
        {
            _browseView!.Refresh();
            return;
        }

        if (_coordinator.Screen == AppScreen.Rank)
        {
            _rankView?.Refresh();
            return;
        }

        var renaming = _coordinator.Screen == AppScreen.Rename;
        _startView!.Refresh();
        if (renaming) _renameView!.Refresh();
        _renameView!.SetShown(renaming); // plan H § 5 S3: the ink card fades in and out
    }

    private Grid StartStack()
    {
        if (_startStack is not null) return _startStack;
        _startView = new StartView(_coordinator!, _motion!);
        _renameView = new RenameView(_coordinator!, _motion!) { IsVisible = false };
        return _startStack = new Grid { Children = { _startView, _renameView } };
    }

    // ---- keys, tunnelled from the TopLevel (plan § 2.2, § 3.7) --------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(InputElement.KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        topLevel?.AddHandler(InputElement.KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);
        topLevel?.AddHandler(InputElement.TextInputEvent, OnTunnelTextInput, RoutingStrategies.Tunnel);
        topLevel?.AddHandler(InputElement.PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnTunnelKeyDown);
        topLevel?.RemoveHandler(InputElement.KeyUpEvent, OnTunnelKeyUp);
        topLevel?.RemoveHandler(InputElement.TextInputEvent, OnTunnelTextInput);
        topLevel?.RemoveHandler(InputElement.PointerPressedEvent, OnTunnelPointerPressed);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Plan H § 5 S3: a key or a click is the owner acting. It ends every decode that is still
    /// running (nobody waits on an animation), and it starts the 1.5 s during which a change counts as his
    /// (so the decode of what his key just changed is not mistaken for something the machine did).
    /// Neither swallows the input: the key or click goes on to do what it does.</summary>
    private void NoteUserAction()
    {
        if (_motion is null) return;
        _motion.Layer.FinishAll();
        _motion.Activity.Note();
    }

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e) => NoteUserAction();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_coordinator is null) return;
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            NoteUserAction();
        var key = KeyMapping.Map(e.Key);
        var modifiers = KeyMapping.MapModifiers(e.KeyModifiers);

        if (_coordinator.Screen == AppScreen.Rank)
        {
            // Plan § 1.1: "anything else -> nothing... swallowed, so Avalonia's own key handling
            // (arrow-key focus navigation, Space/Enter on a focused button) never runs on this screen."
            e.Handled = true;
            _ = _coordinator.OnCompareKeyDown(key, modifiers);
            return;
        }

        if (_coordinator.Screen == AppScreen.Browse)
        {
            // Plan I § 2.2 item 5. A bare modifier is nobody's. With the keys page open every key closes it (and is
            // swallowed, so the letter that closed it is not also typed). Otherwise only the keys KeyMap.MapBrowse
            // knows are ours; letters are left alone so they reach the tunnel's text handler as typed text.
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            if (!_coordinator.Rank.HelpPinned && KeyMap.MapBrowse(key, modifiers) == Intent.None) return;
            e.Handled = true;
            _coordinator.OnBrowseKeyDown(key, modifiers);
            return;
        }

        if (_coordinator.Screen == AppScreen.Rename)
        {
            // § 3.13: "Esc on this screen cancels the rename, not the program". Plan H § 3.2: the ink card
            // has no buttons any more, so Enter is ours too (ENTER RENAME) while the question shows.
            // Everything else is nobody's: the start screen under the veil is disabled.
            if (key == UiKey.Enter)
            {
                e.Handled = true;
                _renameView?.ConfirmIfAllowed();
                return;
            }
            if (key != UiKey.Escape) return;
            e.Handled = true;
            _coordinator.OnRenameEscape();
            return;
        }

        // Start screen. Plan H § 3.3: with the keys page open, any key closes it -- the coordinator
        // decides what "closes" means (Esc included), the key must not also do its normal job. A bare
        // modifier is not a key press for this purpose (Ctrl on its way to Ctrl+Z must not close it).
        if (_coordinator.Rank.HelpPinned)
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            e.Handled = true;
            _coordinator.OnStartKeyDown(key, modifiers);
            return;
        }

        // Plan H § 3.4: ← / → move focus between the pills. Enter/Space/Tab stay Avalonia's own
        // focused-button behaviour (plan § 3.7).
        if (key is UiKey.Left or UiKey.Right)
        {
            e.Handled = true;
            if (!_coordinator.Start.Opening) _startView?.MovePillFocus(key == UiKey.Left ? -1 : 1);
            return;
        }

        // O, R, F1, Esc and Ctrl+Z are ours (plan § 1.1, plan H § 3.4).
        var isOurs = key is UiKey.O or UiKey.R or UiKey.F1 or UiKey.Escape
            || (key == UiKey.Z && modifiers.HasFlag(UiModifiers.Control));
        if (!isOurs) return;

        e.Handled = true;
        _coordinator.OnStartKeyDown(key, modifiers);
    }

    /// <summary>Plan I § 2.2 item 5: on the browser, letters (and digits, spaces) are typed text that finds folders.</summary>
    private void OnTunnelTextInput(object? sender, TextInputEventArgs e)
    {
        if (_coordinator is null || _coordinator.Screen != AppScreen.Browse || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        _coordinator.OnBrowseText(e.Text);
    }

    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        if (_coordinator is null) return;
        var key = KeyMapping.Map(e.Key);
        if (_coordinator.Screen == AppScreen.Rank) _coordinator.OnCompareKeyUp(key);
        else if (_coordinator.Screen == AppScreen.Browse) _coordinator.OnBrowseKeyUp(key);
        else if (_coordinator.Screen != AppScreen.Rename) _coordinator.OnStartKeyUp(key);
    }
}

/// <summary>Implemented by <see cref="StartView"/> and <see cref="RankView"/>: "re-read the
/// coordinator's models and update every control." Called after every <c>Changed</c> instead of
/// full data-binding, because <c>Surface/</c>'s models are deliberately plain data with no
/// <c>INotifyPropertyChanged</c> (plan § 2.1: no Avalonia type in <c>Surface/</c>).</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>The one place <c>Avalonia.Input.Key</c> becomes <see cref="UiKey"/> (plan § 2.1's
/// boundary). Only the keys <c>SPEC.md</c> § Keys and plan § 1.1 name; everything else is
/// <see cref="UiKey.None"/>.</summary>
internal static class KeyMapping
{
    public static UiKey Map(Key key) => key switch
    {
        Key.Left => UiKey.Left,
        Key.Right => UiKey.Right,
        Key.Down => UiKey.Down,
        Key.Up => UiKey.Up,
        Key.S => UiKey.S,
        Key.Z => UiKey.Z,
        Key.O => UiKey.O,
        Key.R => UiKey.R,
        Key.D1 => UiKey.D1,
        Key.D2 => UiKey.D2,
        Key.D3 => UiKey.D3,
        Key.D4 => UiKey.D4,
        Key.D5 => UiKey.D5,
        Key.NumPad1 => UiKey.NumPad1,
        Key.NumPad2 => UiKey.NumPad2,
        Key.NumPad3 => UiKey.NumPad3,
        Key.NumPad4 => UiKey.NumPad4,
        Key.NumPad5 => UiKey.NumPad5,
        Key.F1 => UiKey.F1,
        Key.Escape => UiKey.Escape,
        Key.Enter => UiKey.Enter,
        Key.Space => UiKey.Space,
        Key.Back => UiKey.Backspace,
        Key.PageUp => UiKey.PageUp,
        Key.PageDown => UiKey.PageDown,
        Key.Home => UiKey.Home,
        Key.End => UiKey.End,
        _ => UiKey.None,
    };

    public static UiModifiers MapModifiers(KeyModifiers modifiers)
    {
        var result = UiModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= UiModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= UiModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= UiModifiers.Alt;
        return result;
    }
}
