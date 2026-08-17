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
        ResumeButton.Content = $"Resume  {Path.GetFileName(last.TrimEnd('\\'))}";
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
        StartError.Text = "";
        try
        {
            _pipeline.Dispose();
            _pipeline = CreatePipeline();
            _pipeline.SetFolder(folder);
            UpdatePanelSize();

            var session = new RankingSession(folder, _catalog, _engine, _selector, _pipeline.PrefetchPairs);
            if (!session.Start())
            {
                StartError.Text = "Folder needs at least two supported media files.";
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
            StartError.Text = "Could not open folder: " + ex.Message;
        }
    }

    private void ShowCurrent()
    {
        if (_session?.Current is null)
        {
            StartError.Text = "No pair left to compare.";
            RankPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
            RefreshResumeButton();
            return;
        }

        var pair = _session.Current.Value;
        ResetPane(LeftImage, LeftVideo, LeftSpinner, LeftName, pair.Left, _session.Find(pair.Left));
        ResetPane(RightImage, RightVideo, RightSpinner, RightName, pair.Right, _session.Find(pair.Right));
        OverlayFolder.Text = _session.FolderName;
        OverlayStats.Text = $"Session {_session.SessionVotes}   ·   Unranked {_session.UnrankedCount}";

        _pipeline.Show(pair.Left, pair.Right);
        foreach (var warm in _session.WarmPairs)
            _pipeline.Enqueue(warm);
    }

    private static void ResetPane(
        System.Windows.Controls.Image image,
        MediaElement video,
        TextBlock spinner,
        TextBlock name,
        MediaId id,
        MediaRecord record)
    {
        StopVideo(video);
        image.Source = null;
        name.Text = id.Filename;
        spinner.Visibility = record.Kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnLeftClick(object sender, MouseButtonEventArgs e) => Choose(left: true);
    private void OnRightClick(object sender, MouseButtonEventArgs e) => Choose(left: false);

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
                Choose(left: true);
                e.Handled = true;
                break;
            case Key.Right:
                Choose(left: false);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.S:
                Skip();
                e.Handled = true;
                break;
        }
    }

    private void Choose(bool left)
    {
        if (_busy || _session is null)
            return;
        _busy = true;
        try
        {
            if (left) _session.VoteLeft();
            else _session.VoteRight();
            ShowCurrent();
        }
        finally
        {
            _busy = false;
        }
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
