using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// § 3.13, plan E's reserved slot (§ E6). Two stages of <see cref="RenameModel"/> in one control,
/// swapped by <see cref="Refresh"/> rather than two views, since neither is heavy and the owner
/// never sees both at once: <c>Confirming</c> (the owner's own words, then Yes/No) and
/// <c>Running</c> (<c>done / total</c>, the phase, a real bar — never an indeterminate spinner —
/// and Cancel). <c>Esc</c> for both is wired at <c>UiRoot</c>'s tunnel handler, not here, the same
/// split every other screen already uses; the buttons exist so the mouse works too.
/// </summary>
public partial class RenameView : UserControl, IRefreshable
{
    private const double TrackWidth = 480;

    private readonly RankCoordinator _coordinator;
    private readonly StackPanel _confirmPanel;
    private readonly TextBlock _confirmText;
    private readonly Button _yesButton;
    private readonly Button _noButton;
    private readonly StackPanel _runningPanel;
    private readonly TextBlock _folderText;
    private readonly Border _progressFill;
    private readonly TextBlock _progressText;
    private readonly TextBlock _phaseText;
    private readonly Button _cancelButton;

    public RenameView(RankCoordinator coordinator)
    {
        AvaloniaXamlLoader.Load(this);
        _coordinator = coordinator;

        _confirmPanel = this.FindControl<StackPanel>("ConfirmPanel")!;
        _confirmText = this.FindControl<TextBlock>("ConfirmText")!;
        _yesButton = this.FindControl<Button>("YesButton")!;
        _noButton = this.FindControl<Button>("NoButton")!;
        _runningPanel = this.FindControl<StackPanel>("RunningPanel")!;
        _folderText = this.FindControl<TextBlock>("FolderText")!;
        _progressFill = this.FindControl<Border>("ProgressFill")!;
        _progressText = this.FindControl<TextBlock>("ProgressText")!;
        _phaseText = this.FindControl<TextBlock>("PhaseText")!;
        _cancelButton = this.FindControl<Button>("CancelButton")!;

        _yesButton.Click += (_, _) => _ = _coordinator.ConfirmRenameAsync();
        _noButton.Click += (_, _) => _coordinator.CancelRenameConfirm();
        _cancelButton.Click += (_, _) => _coordinator.RequestCancelRename();

        // Timings.ProgressBarAnimMs (plan E's reserved constant, § E's palette table): a real bar,
        // not a jump cut, as done/total moves on each 250 ms poll.
        _progressFill.Transitions =
        [
            new DoubleTransition { Property = Border.WidthProperty, Duration = Timings.ProgressBarAnimMs },
        ];
    }

    public void Refresh()
    {
        var rename = _coordinator.Rename;
        var confirming = rename.Stage == RenameStage.Confirming;

        _confirmPanel.IsVisible = confirming;
        _runningPanel.IsVisible = !confirming;

        if (confirming)
        {
            _confirmText.Text = rename.ConfirmText;
            _yesButton.IsEnabled = !rename.Busy;
            _noButton.IsEnabled = !rename.Busy;
            if (!rename.Busy) _yesButton.Focus();
            return;
        }

        _folderText.Text = rename.Folder;

        var total = rename.Total;
        var fraction = total > 0 ? Math.Clamp((double)rename.Done / total, 0, 1) : 0;
        _progressFill.Width = TrackWidth * fraction;

        _progressText.Text = total > 0 ? $"{rename.Done} / {total}" : "…";
        _phaseText.Text = rename.CancelRequested ? $"{Phrase(rename.Phase)} — cancelling…" : Phrase(rename.Phase);

        _cancelButton.IsEnabled = !rename.CancelRequested;
        _cancelButton.Content = rename.CancelRequested ? "Cancelling…" : "Cancel  [Esc]";
    }

    private static string Phrase(string phase) => phase switch
    {
        "preparing" => "Preparing…",
        "renaming" => "Renaming…",
        "saving" => "Saving…",
        "reuniting" => "Reuniting ratings with files…",
        "done" => "Done.",
        _ => phase,
    };
}
