using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Shapes;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// The start screen, in house style (plan H § 3.1; mockup C). The last folder's name is the screen
/// (the <b>hero</b>, Doto 64, upper case); under it its path and the <b>pills</b> -- <c>ENTER RESUME</c>,
/// <c>O OPEN</c>, <c>R RENAME</c> -- which are real focusable buttons (the focused one is filled ink; Enter
/// and Space on it are Avalonia's own, ← / → move between them). One status line at the bottom, a
/// server lamp and <c>F1 KEYS</c> in the corner. Resume never auto-starts -- it is a button like any
/// other, just already focused so <c>Enter</c> takes it.
/// <para/>
/// States, all read from <see cref="StartModel"/> and the link in <see cref="Refresh"/>: opening (pills
/// dim, a busy line under the hero, no "Opening…" text), exhausted (label <c>● NO PAIR LEFT</c>, first
/// pill <c>CTRL+Z TAKE BACK</c>), an error (accent status line), a rename that finished (mid status line
/// with an ink dot) and no server (accent lamp and status line; nothing else moves).
/// </summary>
public partial class StartView : UserControl, IRefreshable
{
    private readonly RankCoordinator _coordinator;
    private readonly Ellipse _serverDot;
    private readonly TextBlock _serverText;
    private readonly TextBlock _keysCaption;
    private readonly Ellipse _labelDot;
    private readonly TextBlock _labelText;
    private readonly Viewbox _heroBox;
    private readonly TextBlock _heroText;
    private readonly Grid _underRow;
    private readonly TextBlock _pathText;
    private readonly BusyLine _busy;
    private readonly Button _resumeButton;
    private readonly TextBlock _resumeText;
    private readonly Button _openButton;
    private readonly Button _renameButton;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusLine;
    private readonly KeysPage _keys;

    private bool _focusPending = true;
    private bool _wasOpening;
    private bool _wasExhausted;
    private bool _wasUnderCard;

    public StartView(RankCoordinator coordinator)
    {
        AvaloniaXamlLoader.Load(this);
        _coordinator = coordinator;

        _serverDot = this.FindControl<Ellipse>("ServerDot")!;
        _serverText = this.FindControl<TextBlock>("ServerText")!;
        _keysCaption = this.FindControl<TextBlock>("KeysCaption")!;
        _labelDot = this.FindControl<Ellipse>("LabelDot")!;
        _labelText = this.FindControl<TextBlock>("LabelText")!;
        _heroBox = this.FindControl<Viewbox>("HeroBox")!;
        _heroText = this.FindControl<TextBlock>("HeroText")!;
        _underRow = this.FindControl<Grid>("UnderRow")!;
        _pathText = this.FindControl<TextBlock>("PathText")!;
        _busy = this.FindControl<BusyLine>("Busy")!;
        _resumeButton = this.FindControl<Button>("ResumeButton")!;
        _resumeText = this.FindControl<TextBlock>("ResumeText")!;
        _openButton = this.FindControl<Button>("OpenButton")!;
        _renameButton = this.FindControl<Button>("RenameButton")!;
        _statusDot = this.FindControl<Ellipse>("StatusDot")!;
        _statusLine = this.FindControl<TextBlock>("StatusLine")!;
        _keys = this.FindControl<KeysPage>("Keys")!;

        _openButton.Click += (_, _) => _ = OpenViaPicker();
        _resumeButton.Click += (_, _) =>
        {
            // The first pill is "take back" while an exhausted session is open and can be undone
            // (plan H § 3.1), "resume" otherwise: decided now, from the model, not from the caption.
            if (_coordinator.Start is { MessageKind: StartMessageKind.Exhausted, UndoAvailable: true })
                _ = _coordinator.TryUndoFromStartAsync();
            else if (_coordinator.Start.LastFolder is { } folder)
                _ = _coordinator.OpenFolderAsync(folder);
        };
        _renameButton.Click += (_, _) => _ = RenameViaPicker();

        // F1 by mouse: the same press the key makes, so the coordinator keeps the one rule.
        _keysCaption.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _coordinator.OnStartKeyDown(UiKey.F1, UiModifiers.None);
        };
        _keys.CloseRequested += () => _coordinator.OnStartKeyDown(UiKey.F1, UiModifiers.None);

        _coordinator.OpenFolderRequested += () => _ = OpenViaPicker();
        _coordinator.RenameRequested += () => _ = RenameViaPicker();

        // The hero may be as wide as the screen less a margin; past that it shrinks (Viewbox, DownOnly).
        // The busy line is as wide as the hero (plan H § 3.1), so it follows the hero's width.
        SizeChanged += (_, e) => _heroBox.MaxWidth = Math.Max(120, e.NewSize.Width - 120);
        _heroBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) _busy.Width = _heroBox.Bounds.Width;
        };
        Loaded += (_, _) => Refresh();
    }

    private async Task OpenViaPicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage || _coordinator.Start.DialogOpen) return;

        _coordinator.BeginDialog();
        try
        {
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select a folder to rank" });
            var folder = result.Count > 0 ? result[0].TryGetLocalPath() : null;
            if (folder is not null)
                await _coordinator.OpenFolderAsync(folder);
        }
        finally
        {
            _coordinator.EndDialog();
        }
    }

    /// <summary>§ 3.13: the same native picker <see cref="OpenViaPicker"/> uses, but on a folder to
    /// rename rather than to rank — the result opens the rename card's confirmation, nothing is
    /// sent to the server yet.</summary>
    private async Task RenameViaPicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage || _coordinator.Start.DialogOpen) return;

        _coordinator.BeginDialog();
        try
        {
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select a folder to rename by rank" });
            var folder = result.Count > 0 ? result[0].TryGetLocalPath() : null;
            if (folder is not null)
                _coordinator.BeginRenameConfirm(folder);
        }
        finally
        {
            _coordinator.EndDialog();
        }
    }

    /// <summary>Plan H § 3.4: ← / → move focus between the visible, enabled pills (no wrap: the ends
    /// stay put). With no pill focused yet, either key lands on the first. Called by <see cref="UiRoot"/>'s
    /// tunnel handler, because the pills are the only focusable things here and Avalonia's own arrow
    /// navigation is directional and unpredictable across a row of buttons.</summary>
    public void MovePillFocus(int direction)
    {
        var pills = new[] { _resumeButton, _openButton, _renameButton }.Where(b => b.IsVisible && b.IsEnabled).ToList();
        if (pills.Count == 0) return;
        var current = pills.FindIndex(b => b.IsFocused);
        var next = current < 0 ? 0 : Math.Clamp(current + direction, 0, pills.Count - 1);
        pills[next].Focus(NavigationMethod.Directional);
    }

    /// <summary>The pill that has focus, by name, or null. For tests.</summary>
    public string? FocusedPill => new[] { _resumeButton, _openButton, _renameButton }.FirstOrDefault(b => b.IsFocused)?.Name;

    public void Refresh()
    {
        var start = _coordinator.Start;
        var hasLast = start.LastFolder is not null;
        var exhausted = start.MessageKind == StartMessageKind.Exhausted;
        var underCard = _coordinator.Screen == AppScreen.Rename;

        // Under the rename card the whole screen is inert (plan H § 3.2: drawn, not usable): Tab cannot
        // reach a pill behind the veil. It looks the same -- the pills only dim for "opening".
        IsEnabled = !underCard;

        // ---- corner: server lamp ----
        var lamp = _coordinator.ServerLamp;
        _serverDot.Classes.Set("live", lamp == ServerLamp.Down);
        _serverDot.Classes.Set("off", lamp == ServerLamp.Waiting);
        _serverText.Text = lamp == ServerLamp.Down ? "NO SERVER" : "SERVER";
        _serverText.Classes.Set("accent", lamp == ServerLamp.Down);
        _serverText.Classes.Set("faint", lamp != ServerLamp.Down);

        // ---- label above the hero ----
        _labelDot.IsVisible = exhausted;
        _labelText.Text = exhausted ? "NO PAIR LEFT" : hasLast ? "RESUME" : "";
        _labelText.Classes.Set("accent", exhausted);

        // ---- hero and path ----
        var shown = start.Opening && start.OpeningFolder is { } opening ? opening : start.LastFolder;
        _heroText.Text = shown is null ? "RANK MASTER" : Notices.LeafName(shown).ToUpperInvariant();
        _underRow.IsVisible = hasLast || start.Opening;
        _pathText.Text = start.LastFolder ?? "";
        _pathText.Opacity = start.Opening ? 0 : 1;
        _busy.IsVisible = start.Opening;

        // ---- pills ----
        _resumeButton.IsVisible = hasLast;
        _resumeText.Text = exhausted && start.UndoAvailable ? "CTRL+Z TAKE BACK" : "ENTER RESUME";
        foreach (var pill in new[] { _resumeButton, _openButton, _renameButton })
        {
            pill.IsEnabled = !start.Opening;
            pill.Opacity = start.Opening ? 0.35 : 1;
        }

        // ---- status line: the message if there is one, else what the link says ----
        string? status;
        var statusIsError = false;
        var statusHasDot = false;
        switch (start.MessageKind)
        {
            case StartMessageKind.Error:
                status = start.MessageText;
                statusIsError = true;
                break;
            case StartMessageKind.Done:
                status = start.MessageText;
                statusHasDot = true;
                break;
            default:
                status = _coordinator.LinkStatusLine;
                statusIsError = lamp == ServerLamp.Down;
                break;
        }
        _statusLine.Text = status ?? "";
        _statusLine.Classes.Set("accent", statusIsError);
        _statusLine.Classes.Set("mid", !statusIsError);
        _statusDot.IsVisible = statusHasDot && !string.IsNullOrEmpty(status);

        // ---- the keys page ----
        _keys.SetOpen(_coordinator.Rank.HelpPinned);

        // ---- focus: first pill, once at the start and again whenever it was lost or a state replaced the
        // first pill (opening ended, an exhausted session arrived, the rename card closed). Never steals it
        // from a pill the owner moved to himself.
        if (_wasOpening && !start.Opening) _focusPending = true;
        if (exhausted && !_wasExhausted) _focusPending = true;
        if (_wasUnderCard && !underCard) _focusPending = true;
        _wasOpening = start.Opening;
        _wasExhausted = exhausted;
        _wasUnderCard = underCard;

        if (_focusPending && !start.Opening && !underCard)
        {
            var first = hasLast ? _resumeButton : _openButton;
            if (first.Focus()) _focusPending = false;
        }
    }
}
