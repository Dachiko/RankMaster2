using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// § 3.13, plan E's reserved slot (§ E6), in plan H § 3.2's form: the <b>ink card</b>. <see cref="UiRoot"/>
/// draws this view over the start screen (the veil is this view's own background), for both stages of
/// <see cref="RenameModel"/>, swapped by <see cref="Refresh"/>: <c>Confirming</c> (<c>RENAME BY RANK</c>,
/// the folder in Doto, one line, <c>ENTER RENAME · ESC KEEP</c>) and <c>Running</c> (a pulsing dot and
/// <c>RENAMING · folder</c>, the percent in Doto, 60 dots filling red, <c>ESC CANCEL</c> and the three
/// phase words). Percent only -- never <c>done / total</c>. <c>Esc</c> and <c>Enter</c> are wired at
/// <see cref="UiRoot"/>'s tunnel handler, not here, the same split every other screen uses; the two or three
/// caption words are clickable so the mouse works too (there are no buttons on the card any more).
/// </summary>
public partial class RenameView : UserControl, IRefreshable
{
    private readonly RankCoordinator _coordinator;
    private readonly HouseMotion _motion;
    private readonly FadeHost _fade;
    private readonly Border _card;
    private readonly StackPanel _confirmPanel;
    private readonly TextBlock _confirmFolder;
    private readonly TextBlock _confirmLine;
    private readonly TextBlock _yesCaption;
    private readonly TextBlock _noCaption;
    private readonly StackPanel _runningPanel;
    private readonly TextBlock _runningFolder;
    private readonly TextBlock _percentText;
    private readonly DotRow _dots;
    private readonly BusyLine _cancellingLine;
    private readonly TextBlock _cancelCaption;
    private readonly TextBlock[] _phaseWords;

    public RenameView(RankCoordinator coordinator, HouseMotion motion)
    {
        AvaloniaXamlLoader.Load(this);
        _coordinator = coordinator;
        _motion = motion;
        _fade = new FadeHost(this);

        _card = this.FindControl<Border>("Card")!;
        _confirmPanel = this.FindControl<StackPanel>("ConfirmPanel")!;
        _confirmFolder = this.FindControl<TextBlock>("ConfirmFolder")!;
        _confirmLine = this.FindControl<TextBlock>("ConfirmLine")!;
        _yesCaption = this.FindControl<TextBlock>("YesCaption")!;
        _noCaption = this.FindControl<TextBlock>("NoCaption")!;
        _runningPanel = this.FindControl<StackPanel>("RunningPanel")!;
        _runningFolder = this.FindControl<TextBlock>("RunningFolder")!;
        _percentText = this.FindControl<TextBlock>("PercentText")!;
        _dots = this.FindControl<DotRow>("Dots")!;
        _cancellingLine = this.FindControl<BusyLine>("CancellingLine")!;
        _cancelCaption = this.FindControl<TextBlock>("CancelCaption")!;
        _phaseWords =
        [
            this.FindControl<TextBlock>("PhasePrepare")!,
            this.FindControl<TextBlock>("PhaseRename")!,
            this.FindControl<TextBlock>("PhaseSave")!,
        ];

        // Plan H § 5 S3: "the red dots fill". Filled counts dots, so the transition steps from one dot to the
        // next as the percent moves (the same 200 ms the old progress bar took, Timings.ProgressBarAnimMs).
        _dots.Transitions =
        [
            new DoubleTransition { Property = DotRow.FilledProperty, Duration = Timings.ProgressBarAnimMs },
        ];

        _yesCaption.PointerPressed += (_, e) => { e.Handled = true; ConfirmIfAllowed(); };
        _noCaption.PointerPressed += (_, e) => { e.Handled = true; _coordinator.CancelRenameConfirm(); };
        _cancelCaption.PointerPressed += (_, e) => { e.Handled = true; _coordinator.RequestCancelRename(); };
    }

    /// <summary>What was last asked for; the card's own <c>IsVisible</c> lags a hide by the fade.</summary>
    public bool IsShown => _fade.IsShown;

    /// <summary>The ink card fades in when the rename screen starts and out when it ends (plan H § 5 S3).</summary>
    public void SetShown(bool shown)
    {
        if (shown) _fade.Show();
        else _fade.Hide();
    }

    /// <summary>Enter, or a click on <c>ENTER RENAME</c>: start the rename, unless one call is already
    /// in flight (the coordinator's own guard against overlapping calls on the one-call-at-a-time link).</summary>
    public void ConfirmIfAllowed()
    {
        var rename = _coordinator.Rename;
        if (rename.Stage != RenameStage.Confirming || rename.Busy) return;
        _ = _coordinator.ConfirmRenameAsync();
    }

    public void Refresh()
    {
        var rename = _coordinator.Rename;
        var confirming = rename.Stage == RenameStage.Confirming;

        _confirmPanel.IsVisible = confirming;
        _runningPanel.IsVisible = !confirming;

        if (confirming)
        {
            // Everything this card shows is the owner's doing (he picked the folder, he pressed Enter), so
            // its text decodes in rather than swapping silently (plan H § 5 S3).
            _motion.Change(_confirmFolder, rename.FolderName.ToUpperInvariant(), userCaused: true);
            _confirmLine.Text = RenameModel.ConfirmLine;
            return;
        }

        _runningFolder.Text = "  ·  " + rename.FolderName.ToUpperInvariant();

        // The percent rolls digit by digit: 36 → 37 rolls the last digit, 99 → 100 all three.
        _motion.Change(_percentText, rename.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            userCaused: true, onlyChangedLetters: true);
        _dots.Filled = DotRow.DotsFor(rename.Percent, _dots.Count);

        // Cancel asked for: the left caption says so and a busy line takes the dots' place (plan H § 3.2).
        _cancelCaption.Text = rename.CancelRequested ? "CANCELLING" : "ESC CANCEL";
        _dots.IsVisible = !rename.CancelRequested;
        _cancellingLine.IsVisible = rename.CancelRequested;

        for (var i = 0; i < _phaseWords.Length; i++)
        {
            _phaseWords[i].Classes.Set("accent", i == rename.PhaseStep);
            _phaseWords[i].Classes.Set("onink60", i != rename.PhaseStep);
        }
    }
}
