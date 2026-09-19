using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Link.Wire;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>Plan § 1.2, § 4.3: folder, confidence percent, bar, unranked, session. The only
/// progress figure on this screen -- "a quiet percent only", no tier name, no hairline.</summary>
public partial class InfoCard : UserControl
{
    // Matches the confidence track's Width in InfoCard.axaml -- keep the two in step.
    private const double ConfidenceBarMaxWidth = 48;

    private readonly TextBlock _folderText;
    private readonly TextBlock _confidenceText;
    private readonly Border _confidenceBar;
    private readonly TextBlock _unrankedText;
    private readonly TextBlock _sessionText;

    public InfoCard()
    {
        AvaloniaXamlLoader.Load(this);
        _folderText = this.FindControl<TextBlock>("FolderText")!;
        _confidenceText = this.FindControl<TextBlock>("ConfidenceText")!;
        _confidenceBar = this.FindControl<Border>("ConfidenceBar")!;
        _unrankedText = this.FindControl<TextBlock>("UnrankedText")!;
        _sessionText = this.FindControl<TextBlock>("SessionText")!;
    }

    public void Render(Snapshot snapshot)
    {
        _folderText.Text = snapshot.FolderName;
        _confidenceText.Text = $"{snapshot.ProgressPercent}%";
        _confidenceBar.Width = ConfidenceBarMaxWidth * (snapshot.ProgressPercent / 100.0);
        _unrankedText.Text = snapshot.Counts.Unranked.ToString();
        _sessionText.Text = snapshot.SessionVotes.ToString();
    }
}
