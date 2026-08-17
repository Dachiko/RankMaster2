using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RankMaster2.Catalog;
using RankMaster2.Ranking;

namespace RankMaster2;

public partial class MainWindow : Window
{
    private readonly JsonCatalog _catalog = new();
    private readonly TrueSkill _engine = new();
    private readonly PairSelector _selector = new();
    private MediaPipeline _pipeline;
    private RankingSession? _session;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _pipeline = CreatePipeline();
        ApplyExclusiveFullscreen();
        RefreshResumeButton();
        Closed += (_, _) => _pipeline.Dispose();
    }

    private MediaPipeline CreatePipeline()
    {
        var pipeline = new MediaPipeline(MediaPipeline.DefaultPrefetchPairs, Dispatcher);
        pipeline.Ready += OnFrameReady;
        pipeline.Failed += OnFrameFailed;
        return pipeline;
    }

    private void ApplyExclusiveFullscreen()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    private void RefreshResumeButton()
    {
        var last = LastFolderStore.Load();
        if (last is null)
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            return;
        }

        ResumeButton.Visibility = Visibility.Visible;
        ResumeHint.Text = Path.GetFileName(last.TrimEnd('\\'));
        ResumeButton.Tag = last;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => PickFolder();

    private void OnResume(object sender, RoutedEventArgs e)
    {
        if (ResumeButton.Tag is string path)
            BeginSession(path);
    }

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to rank" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (!string.IsNullOrWhiteSpace(dialog.FolderName))
            BeginSession(dialog.FolderName);
    }

    private void BeginSession(string folder)
    {
        SetStartError("");
        try
        {
            _pipeline.Dispose();
            _pipeline = CreatePipeline();
            _pipeline.SetFolder(folder);
            UpdatePanelSize();

            var session = new RankingSession(folder, _catalog, _engine, _selector, _pipeline.PrefetchPairs);
            if (!session.Start())
            {
                SetStartError("Folder needs at least two supported media files.");
                StartPanel.Visibility = Visibility.Visible;
                RankPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _session = session;
            LastFolderStore.Save(folder);
            StartPanel.Visibility = Visibility.Collapsed;
            RankPanel.Visibility = Visibility.Visible;
            ShowCurrent();
        }
        catch (Exception ex)
        {
            SetStartError("Could not open folder: " + ex.Message);
        }
    }

    private void SetStartError(string message)
    {
        StartError.Text = message;
        StartErrorBox.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCurrent()
    {
        if (_session?.Current is null)
        {
            SetStartError("No pair left to compare.");
            RankPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
            RefreshResumeButton();
            return;
        }

        ResetPaneTransforms();
        var pair = _session.Current.Value;
        ResetPane(LeftImage, LeftVideo, LeftSpinner, LeftName, pair.Left);
        ResetPane(RightImage, RightVideo, RightSpinner, RightName, pair.Right);
        OverlayFolder.Text = _session.FolderName;
        OverlayUnranked.Text = _session.UnrankedCount.ToString("N0");
        OverlaySession.Text = _session.SessionVotes.ToString("N0");
        var progress = LibraryProgress.Of(_session.Records);
        OverlayConfidencePct.Text = $"{Math.Round(progress * 100)}%";
        ConfidenceFill.Width = 232 * progress;

        _pipeline.Show(pair.Left, pair.Right);
        foreach (var warm in _session.WarmPairs)
            _pipeline.Enqueue(warm);
    }

    private static void ResetPane(
        System.Windows.Controls.Image image,
        MediaElement video,
        TextBlock spinner,
        TextBlock name,
        MediaId id)
    {
        StopVideo(video);
        image.Source = null;
        name.Text = id.Filename;
        spinner.Visibility = Visibility.Visible;
    }

    private void OnFrameReady(MediaId id, PreparedFrame frame)
    {
        if (_session?.Current is null)
            return;
        var pair = _session.Current.Value;
        if (pair.Left == id)
            ApplyFrame(LeftImage, LeftVideo, LeftSpinner, frame);
        else if (pair.Right == id)
            ApplyFrame(RightImage, RightVideo, RightSpinner, frame);
    }

    private void OnFrameFailed(MediaId id)
    {
        if (_session?.Current is { } pair && pair.Contains(id) && !_busy)
        {
            _session.AbandonCurrent();
            ShowCurrent();
        }
    }

    private static void ApplyFrame(
        System.Windows.Controls.Image image,
        MediaElement video,
        TextBlock spinner,
        PreparedFrame frame)
    {
        if (frame.Kind == MediaKind.Video)
        {
            if (video.Source?.OriginalString == frame.VideoPath)
                return;
            StopVideo(video);
            video.Source = frame.VideoPath is null ? null : new Uri(frame.VideoPath);
            try { video.Play(); } catch { /* MediaOpened will retry */ }
            return;
        }

        StopVideo(video);
        image.Source = frame.Still;
        spinner.Visibility = Visibility.Collapsed;
    }

    private static void StopVideo(MediaElement video)
    {
        try { video.Stop(); } catch { /* not opened */ }
        video.Source = null;
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement el)
        {
            el.Position = TimeSpan.Zero;
            el.Play();
        }
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (sender == LeftVideo)
            LeftSpinner.Visibility = Visibility.Collapsed;
        if (sender == RightVideo)
            RightSpinner.Visibility = Visibility.Collapsed;
        if (sender is MediaElement el)
        {
            el.Volume = 0;
            el.IsMuted = true;
            el.Play();
        }
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e) => _ = ChooseAsync(left: true);
    private void OnRightClick(object sender, MouseButtonEventArgs e) => _ = ChooseAsync(left: false);

    private void OnHelpEnter(object sender, MouseEventArgs e) => HelpPanel.Visibility = Visibility.Visible;
    private void OnHelpLeave(object sender, MouseEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;

    private void OnDiscardLeft(object sender, RoutedEventArgs e) { /* wired in the actions slice */ }
    private void OnDiscardRight(object sender, RoutedEventArgs e) { }
    private void OnSpecialLeft(object sender, RoutedEventArgs e) { }
    private void OnSpecialRight(object sender, RoutedEventArgs e) { }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Application.Current.Shutdown();
            return;
        }

        if (e.Key == Key.O)
        {
            e.Handled = true;
            PickFolder();
            return;
        }

        if (RankPanel.Visibility != Visibility.Visible || _session is null)
            return;

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            _session.Save();
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                _ = ChooseAsync(left: true);
                e.Handled = true;
                break;
            case Key.Right:
                _ = ChooseAsync(left: false);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.S:
                Skip();
                e.Handled = true;
                break;
        }
    }

    private async Task ChooseAsync(bool left)
    {
        if (_busy || _session is null)
            return;
        _busy = true;
        try
        {
            if (left)
            {
                LeftScale.ScaleX = LeftScale.ScaleY = 0.95;
                RightPane.Opacity = 0.4;
            }
            else
            {
                RightScale.ScaleX = RightScale.ScaleY = 0.95;
                LeftPane.Opacity = 0.4;
            }

            await Task.Delay(150);
            if (left) _session.VoteLeft();
            else _session.VoteRight();
            ShowCurrent();
        }
        finally
        {
            ResetPaneTransforms();
            _busy = false;
        }
    }

    private void ResetPaneTransforms()
    {
        LeftScale.ScaleX = LeftScale.ScaleY = 1;
        RightScale.ScaleX = RightScale.ScaleY = 1;
        LeftPane.Opacity = 1;
        RightPane.Opacity = 1;
    }

    private void Skip()
    {
        if (_busy || _session is null)
            return;
        _busy = true;
        try
        {
            _session.Skip();
            ShowCurrent();
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRankSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePanelSize();

    private void UpdatePanelSize()
    {
        if (RankPanel.ActualWidth < 32 || RankPanel.ActualHeight < 32)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)((RankPanel.ActualWidth / 2.0) * dpi.DpiScaleX);
        var h = (int)(RankPanel.ActualHeight * dpi.DpiScaleY);
        _pipeline.SetPanelPixelSize(w, h);
    }
}
