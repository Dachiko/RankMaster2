using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// What part A hosts as window content (plan § 2.2). Switches between <see cref="StartView"/> and
/// <see cref="RankView"/> as <see cref="RankCoordinator.Screen"/> changes, attaches the key handlers
/// to the <see cref="TopLevel"/> in the tunnel phase (plan § 2.2, § 3.7), and exposes
/// <see cref="QuitRequested"/>. Decides nothing itself -- every decision is <c>Surface/</c>'s.
/// </summary>
public partial class UiRoot : UserControl, IUiThread
{
    private readonly RankCoordinator? _coordinator;
    private StartView? _startView;
    private RankView? _rankView;
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

        if (_renderedScreen != _coordinator.Screen)
        {
            _renderedScreen = _coordinator.Screen;
            root.Content = _coordinator.Screen == AppScreen.Rank
                ? _rankView ??= new RankView(_coordinator)
                : _startView ??= new StartView(_coordinator);
        }

        (root.Content as IRefreshable)?.Refresh();
    }

    // ---- keys, tunnelled from the TopLevel (plan § 2.2, § 3.7) --------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(InputElement.KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        topLevel?.AddHandler(InputElement.KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnTunnelKeyDown);
        topLevel?.RemoveHandler(InputElement.KeyUpEvent, OnTunnelKeyUp);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_coordinator is null) return;
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

        // Start screen: only O, F1, Esc and Ctrl+Z are ours (plan § 1.1, § 3.7); everything else,
        // including Enter/Space/Tab, is Avalonia's own focused-button behaviour.
        var isOurs = key is UiKey.O or UiKey.F1 or UiKey.Escape
            || (key == UiKey.Z && modifiers.HasFlag(UiModifiers.Control));
        if (!isOurs) return;

        e.Handled = true;
        _coordinator.OnStartKeyDown(key, modifiers);
    }

    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        if (_coordinator is null) return;
        var key = KeyMapping.Map(e.Key);
        if (_coordinator.Screen == AppScreen.Rank) _coordinator.OnCompareKeyUp(key);
        else _coordinator.OnStartKeyUp(key);
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
