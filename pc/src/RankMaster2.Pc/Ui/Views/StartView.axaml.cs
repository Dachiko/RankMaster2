using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// The start screen (plan § 4.1): Open, Resume (focused when shown), the link status line, the red
/// box for open refusals and the exhausted sentence, and the green box for a rename that finished
/// as asked. Resume never auto-starts -- it is a button like any other, just already focused so
/// <c>Enter</c> takes it.
/// </summary>
public partial class StartView : UserControl, IRefreshable
{
    private readonly RankCoordinator _coordinator;
    private readonly TextBlock _versionText;
    private readonly Button _openButton;
    private readonly Button _resumeButton;
    private readonly Button _renameButton;
    private readonly TextBlock _openingLine;
    private readonly Border _errorBox;
    private readonly TextBlock _errorText;
    private readonly Border _successBox;
    private readonly TextBlock _successText;
    private readonly TextBlock _statusLine;
    private readonly Border _helpButton;
    private readonly HelpSheet _help;

    private bool _resumeFocusedOnce;

    public StartView(RankCoordinator coordinator)
    {
        AvaloniaXamlLoader.Load(this);
        _coordinator = coordinator;

        _versionText = this.FindControl<TextBlock>("VersionText")!;
        _openButton = this.FindControl<Button>("OpenButton")!;
        _resumeButton = this.FindControl<Button>("ResumeButton")!;
        _renameButton = this.FindControl<Button>("RenameButton")!;
        _openingLine = this.FindControl<TextBlock>("OpeningLine")!;
        _errorBox = this.FindControl<Border>("ErrorBox")!;
        _errorText = this.FindControl<TextBlock>("ErrorText")!;
        _successBox = this.FindControl<Border>("SuccessBox")!;
        _successText = this.FindControl<TextBlock>("SuccessText")!;
        _statusLine = this.FindControl<TextBlock>("StatusLine")!;
        _helpButton = this.FindControl<Border>("HelpButton")!;
        _help = this.FindControl<HelpSheet>("Help")!;

        _versionText.Text = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "";
        _help.SetVersion(_versionText.Text);

        _openButton.Click += (_, _) => _ = OpenViaPicker();
        _resumeButton.Click += (_, _) =>
        {
            if (_coordinator.Start.LastFolder is { } folder) _ = _coordinator.OpenFolderAsync(folder);
        };
        _renameButton.Click += (_, _) => _ = RenameViaPicker();

        _helpButton.PointerEntered += (_, _) => _help.SetButtonHover(true);
        _helpButton.PointerExited += (_, _) => _help.SetButtonHover(false);

        _coordinator.OpenFolderRequested += () => _ = OpenViaPicker();
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
    /// rename rather than to rank — the result opens the rename screen's confirmation, nothing is
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

    public void Refresh()
    {
        var start = _coordinator.Start;

        _resumeButton.IsVisible = start.LastFolder is not null;
        if (start.LastFolder is not null)
        {
            _resumeButton.Content = $"Resume  {System.IO.Path.GetFileName(start.LastFolder.TrimEnd('/', '\\'))}";
            if (!_resumeFocusedOnce)
            {
                _resumeFocusedOnce = true;
                _resumeButton.Focus();
            }
        }

        _openButton.IsEnabled = !start.Opening;
        _resumeButton.IsEnabled = !start.Opening;
        _renameButton.IsEnabled = !start.Opening;

        _openingLine.IsVisible = start.Opening;
        _openingLine.Text = start.Opening ? $"Opening {start.OpeningFolder}…" : "";

        var hasBox = !string.IsNullOrEmpty(start.BoxText);
        _errorBox.IsVisible = hasBox && start.BoxIsError;
        _errorText.Text = start.BoxIsError ? start.BoxText ?? "" : "";
        _successBox.IsVisible = hasBox && !start.BoxIsError;
        _successText.Text = start.BoxIsError ? "" : start.BoxText ?? "";

        var status = _coordinator.LinkStatusLine;
        _statusLine.IsVisible = !string.IsNullOrEmpty(status);
        _statusLine.Text = status ?? "";

        _help.SetPinned(_coordinator.Rank.HelpPinned);
    }
}
