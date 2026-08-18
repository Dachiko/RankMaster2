using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using RankMaster2.Catalog;
using RankMaster2.Ranking;

namespace RankMaster2;

public partial class MainWindow : Window
{
    private readonly JsonCatalog _catalog = new();
    private readonly TrueSkill _engine = new();
    private readonly PairSelector _selector = new();
    private readonly LibraryActions _actions;
    private MediaPipeline _pipeline;
    private RankingSession? _session;
    private bool _busy;
    private bool _exiting;
    private bool _renaming;
    private bool _inShowCurrent;
    private bool _helpPinned;
    private readonly VlcRuntime _vlc;
    private readonly VlcFramePlayer _leftVideo;
    private readonly VlcFramePlayer _rightVideo;
    private Storyboard? _selectCue;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _loadTimer;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = AppInfo.Version;
        HelpVersion.Text = "RankMaster 2  ·  " + AppInfo.Version;
        _pipeline = CreatePipeline();
        _actions = new LibraryActions(
            folder: () => _session?.Folder ?? LastFolderStore.Load() ?? "",
            catalog: _catalog,
            pipeline: () => _pipeline,
            session: () => _session,
            releaseUi: ReleaseUi);
        _vlc = new VlcRuntime();
        _leftVideo = CreatePlayer(LeftImage, LeftLoad);
        _rightVideo = CreatePlayer(RightImage, RightLoad);
        ApplyExclusiveFullscreen();
        RefreshResumeButton();
        _loadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _loadTimer.Tick += (_, _) => RefreshLoadProgress();
        Closed += (_, _) =>
        {
            CloseHelp();
            _loadTimer.Stop();
            _leftVideo.Dispose();
            _rightVideo.Dispose();
            _vlc.Dispose();
            _pipeline.Dispose();
        };
    }

    private VlcFramePlayer CreatePlayer(System.Windows.Controls.Image image, UIElement load)
    {
        var player = new VlcFramePlayer(_vlc, Dispatcher);
        player.FirstFrame += bmp =>
        {
            image.Source = bmp;
            load.Visibility = Visibility.Collapsed;
        };
        player.Failed += OnVideoFailed;
        return player;
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
        WindowState = WindowState.Maximized;
    }

    private void RefreshResumeButton()
    {
        var last = LastFolderStore.Load();
        if (last is null)
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            RenameButton.Visibility = Visibility.Collapsed;
            return;
        }

        ResumeButton.Visibility = Visibility.Visible;
        ResumeHint.Text = Path.GetFileName(last.TrimEnd('\\'));
        ResumeButton.Tag = last;
        RenameButton.Visibility = Visibility.Visible;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (!_renaming)
            PickFolder();
    }

    private void OnResume(object sender, RoutedEventArgs e)
    {
        if (_renaming)
            return;
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
        if (_renaming)
            return;
        SetStartError("");
        RankingSession next;
        try
        {
            next = new RankingSession(folder, _catalog, _engine, _selector, MediaPipeline.DefaultPrefetchPairs);
            if (!next.Start())
            {
                SetStartError("Folder needs at least two supported media files.");
                return;
            }
        }
        catch (Exception ex)
        {
            SetStartError("Could not open folder: " + ex.Message);
            return;
        }

        LeaveCompare();
        _pipeline.Ready -= OnFrameReady;
        _pipeline.Failed -= OnFrameFailed;
        _pipeline.Dispose();
        _pipeline = CreatePipeline();
        _pipeline.SetFolder(folder);

        _session = next;
        _actions.ClearLastMove();
        LastFolderStore.Save(folder);
        StartPanel.Visibility = Visibility.Collapsed;
        RankPanel.Visibility = Visibility.Visible;
        RankPanel.UpdateLayout();
        UpdatePanelSize();
        ShowCurrent();
    }

    private void LeaveCompare()
    {
        _leftVideo.Stop();
        _rightVideo.Stop();
        LeftImage.Source = null;
        RightImage.Source = null;
        LeftLoad.Visibility = Visibility.Collapsed;
        RightLoad.Visibility = Visibility.Collapsed;
        _loadTimer?.Stop();
        try { _pipeline.ReleaseAll(); } catch { /* disposed */ }
    }

    private void SetStartError(string message)
    {
        StartError.Text = message;
        StartErrorBox.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCurrent()
    {
        if (_inShowCurrent)
            return;
        _inShowCurrent = true;
        try
        {
            if (_session?.Current is null)
            {
                LeaveCompare();
                SetStartError("No pair left to compare.");
                RankPanel.Visibility = Visibility.Collapsed;
                StartPanel.Visibility = Visibility.Visible;
                RefreshResumeButton();
                return;
            }

            SetStartError("");
            StartPanel.Visibility = Visibility.Collapsed;
            RankPanel.Visibility = Visibility.Visible;
            RankPanel.UpdateLayout();
            UpdatePanelSize();

            ResetPaneTransforms();
            var pair = _session.Current.Value;
            ResetPane(LeftImage, _leftVideo, LeftLoad, LeftLoadPct, LeftName, pair.Left);
            ResetPane(RightImage, _rightVideo, RightLoad, RightLoadPct, RightName, pair.Right);
            _loadTimer?.Start();
            OverlayFolder.Text = _session.FolderName;
            OverlayUnranked.Text = _session.UnrankedCount.ToString("N0");
            OverlaySession.Text = _session.SessionVotes.ToString("N0");
            var progress = LibraryProgress.Of(_session.Rankable);
            OverlayConfidencePct.Text = $"{Math.Round(progress * 100)}%";
            ConfidenceFill.Width = 232 * progress;
            RefreshMatchStrip();

            _pipeline.Show(pair.Left, pair.Right);
            foreach (var warm in _session.WarmPairs)
                _pipeline.Enqueue(warm);
        }
        finally
        {
            _inShowCurrent = false;
        }
    }

    private void ResetPane(
        System.Windows.Controls.Image image,
        VlcFramePlayer player,
        UIElement load,
        TextBlock loadPct,
        TextBlock name,
        MediaId id)
    {
        player.Stop();
        image.Source = null;
        name.Text = id.Filename;
        loadPct.Text = "0%";
        load.Visibility = Visibility.Visible;
    }

    private void OnFrameReady(MediaId id, PreparedFrame frame)
    {
        if (_session?.Current is null)
            return;
        var pair = _session.Current.Value;
        if (pair.Left == id)
            ApplyFrame(LeftImage, _leftVideo, LeftLoad, frame);
        else if (pair.Right == id)
            ApplyFrame(RightImage, _rightVideo, RightLoad, frame);
    }

    private void OnFrameFailed(MediaId id)
    {
        if (_session is null || _busy || _inShowCurrent)
            return;
        if (_session.Current is not { } pair || !pair.Contains(id))
            return;
        _session.Drop(id);
        ShowCurrent();
    }

    private void OnVideoFailed(string path)
    {
        if (_exiting || RankPanel.Visibility != Visibility.Visible)
            return;
        OnFrameFailed(new MediaId(Path.GetFileName(path)));
    }

    private void ApplyFrame(
        System.Windows.Controls.Image image,
        VlcFramePlayer player,
        UIElement load,
        PreparedFrame frame)
    {
        if (frame.Kind == MediaKind.Video)
        {
            if (frame.VideoPath is null)
                return;
            if (player.Opened && PathsMatch(player.ExpectedPath, frame.VideoPath))
                return;
            load.Visibility = Visibility.Visible;
            player.Play(frame.VideoPath);
            return;
        }

        player.Stop();
        image.Source = frame.Still;
        load.Visibility = Visibility.Collapsed;
    }

    private static bool PathsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (Exception)
        {
            // not a filesystem path
        }

        return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
    }

    private bool BothPanesReady()
    {
        if (_session?.Current is not { } pair)
            return false;
        return PaneReady(pair.Left, LeftImage, _leftVideo) &&
               PaneReady(pair.Right, RightImage, _rightVideo);
    }

    private static bool PaneReady(MediaId id, System.Windows.Controls.Image image, VlcFramePlayer player)
    {
        var kind = MediaExtensions.KindOf(id.Filename);
        if (kind == MediaKind.Video)
            return player.Opened;
        return image.Source is not null;
    }

    private void RefreshLoadProgress()
    {
        if (RankPanel.Visibility != Visibility.Visible)
        {
            _loadTimer?.Stop();
            return;
        }

        UpdateLoadChrome(_leftVideo, LeftLoad, LeftLoadPct, LeftImage);
        UpdateLoadChrome(_rightVideo, RightLoad, RightLoadPct, RightImage);
        if (BothPanesReady())
            _loadTimer?.Stop();
    }

    private static void UpdateLoadChrome(
        VlcFramePlayer player,
        UIElement load,
        TextBlock pct,
        System.Windows.Controls.Image image)
    {
        if (load.Visibility != Visibility.Visible)
            return;
        if (player.Opened || image.Source is not null)
        {
            load.Visibility = Visibility.Collapsed;
            return;
        }

        pct.Text = $"{Math.Clamp((int)Math.Round(player.BufferPercent), 0, 99)}%";
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (BothPanesReady())
            _ = ChooseAsync(left: true);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (BothPanesReady())
            _ = ChooseAsync(left: false);
    }

    private void OnHelpEnter(object sender, MouseEventArgs e) => SetHelpOpen(true);

    private void OnHelpLeave(object sender, MouseEventArgs e) =>
        Dispatcher.BeginInvoke(HideHelpIfUnpinned, DispatcherPriority.Input);

    private void HideHelpIfUnpinned()
    {
        if (_helpPinned || _exiting)
            return;
        if (HelpButton.IsMouseOver)
            return;
        if (HelpPopup.IsOpen && HelpPopup.Child is { IsMouseOver: true })
            return;
        SetHelpOpen(false);
    }

    private void ToggleHelp()
    {
        if (HelpPopup.IsOpen)
        {
            _helpPinned = false;
            SetHelpOpen(false);
        }
        else
        {
            _helpPinned = true;
            SetHelpOpen(true);
        }
    }

    private void SetHelpOpen(bool open)
    {
        if (HelpPopup.IsOpen == open)
            return;
        HelpPopup.IsOpen = open;
    }

    private void CloseHelp()
    {
        _helpPinned = false;
        SetHelpOpen(false);
    }

    private void OnDiscardLeft(object sender, RoutedEventArgs e) => MoveCurrent(left: true, special: false);
    private void OnDiscardRight(object sender, RoutedEventArgs e) => MoveCurrent(left: false, special: false);
    private void OnSpecialLeft(object sender, RoutedEventArgs e) => MoveCurrent(left: true, special: true);
    private void OnSpecialRight(object sender, RoutedEventArgs e) => MoveCurrent(left: false, special: true);

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        var folder = LastFolderStore.Load();
        if (folder is null)
            return;

        var confirm = MessageBox.Show(
            this,
            "Backup this folder, then rename files to 000001, 000002, … by rank (μ − 3σ).\n\nContinue?",
            "Rename Files by Rank",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        _renaming = true;
        CloseHelp();
        LeaveCompare();
        RenamePanel.Visibility = Visibility.Visible;
        RenameStatus.Text = "Backing up and renaming…";
        try
        {
            await Task.Run(() => _actions.RenameByRank());
            SetStartError("");
            ShowToast("Renamed files by rank.");
        }
        catch (Exception ex)
        {
            SetStartError(ex.Message);
        }
        finally
        {
            RenamePanel.Visibility = Visibility.Collapsed;
            _renaming = false;
        }
    }

    private void MoveCurrent(bool left, bool special)
    {
        if (_busy || _session?.Current is null || !BothPanesReady())
            return;
        _busy = true;
        try
        {
            var id = left ? _session.Current.Value.Left : _session.Current.Value.Right;
            var name = id.Filename;
            if (special) _actions.MoveToSpecial(id);
            else _actions.Discard(id);
            ShowToast(special ? $"Moved {name} to special 1" : $"Discarded {name}");
            ShowCurrent();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            ShowCurrent();
        }
        finally
        {
            _busy = false;
        }
    }

    private void UndoMove()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            if (_actions.UndoLastMove())
            {
                ShowToast("Move undone");
                ShowCurrent();
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ReleaseUi(MediaId id)
    {
        if (_session?.Current is not { } pair)
            return;
        if (pair.Left == id)
        {
            _leftVideo.Stop();
            LeftImage.Source = null;
        }
        if (pair.Right == id)
        {
            _rightVideo.Stop();
            RightImage.Source = null;
        }
    }

    private void RefreshMatchStrip()
    {
        var cues = _session?.RecentCues;
        if (cues is null || cues.Count == 0)
        {
            MatchStrip.Visibility = Visibility.Collapsed;
            MatchStripBalls.Children.Clear();
            return;
        }

        MatchStripBalls.Children.Clear();
        foreach (var cue in cues)
            MatchStripBalls.Children.Add(MakeMatchBall(cue));
        MatchStrip.Visibility = Visibility.Visible;
    }

    private void AnimateLastMatchBall()
    {
        if (MatchStripBalls.Children.Count == 0)
            return;
        if (MatchStripBalls.Children[^1] is not System.Windows.Shapes.Ellipse ball)
            return;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var story = new Storyboard();
        story.Children.Add(Anim(ball, UIElement.OpacityProperty, 0, 1, 300, ease));
        if (ball.RenderTransform is TransformGroup g &&
            g.Children.Count >= 2 &&
            g.Children[0] is ScaleTransform scale &&
            g.Children[1] is TranslateTransform slide)
        {
            story.Children.Add(Anim(scale, ScaleTransform.ScaleXProperty, 0.4, 1, 300, ease));
            story.Children.Add(Anim(scale, ScaleTransform.ScaleYProperty, 0.4, 1, 300, ease));
            story.Children.Add(Anim(slide, TranslateTransform.XProperty, 16, 0, 300, ease));
        }

        story.Begin();
    }

    private static System.Windows.Shapes.Ellipse MakeMatchBall(MatchCue cue)
    {
        var color = cue == MatchCue.Confirmation
            ? Color.FromArgb(0xB3, 0x10, 0xB9, 0x81)
            : Color.FromArgb(0xB3, 0xF5, 0x9E, 0x0B);
        var scale = new ScaleTransform(1, 1);
        var slide = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(slide);
        return new System.Windows.Shapes.Ellipse
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(4, 0, 4, 0),
            Fill = new SolidColorBrush(color),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = group,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 0,
                Color = Colors.Black,
                Opacity = 0.5
            }
        };
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_renaming)
                return;
            _exiting = true;
            CloseHelp();
            StopSelectCue();
            Application.Current.Shutdown();
            return;
        }

        if (e.Key == Key.F1)
        {
            e.Handled = true;
            if (!_renaming)
                ToggleHelp();
            return;
        }

        if (e.Key == Key.O)
        {
            e.Handled = true;
            if (!_renaming)
                PickFolder();
            return;
        }

        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            if (_session is not null && !_renaming)
                UndoMove();
            return;
        }

        if (RankPanel.Visibility != Visibility.Visible || _session is null)
            return;

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            try { _session.Save(); }
            catch (Exception ex) { ShowToast(ex.Message); }
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
            case Key.D1:
            case Key.NumPad1:
                MoveCurrent(left: true, special: false);
                e.Handled = true;
                break;
            case Key.D2:
            case Key.NumPad2:
                MoveCurrent(left: false, special: false);
                e.Handled = true;
                break;
            case Key.D4:
            case Key.NumPad4:
                MoveCurrent(left: true, special: true);
                e.Handled = true;
                break;
            case Key.D5:
            case Key.NumPad5:
                MoveCurrent(left: false, special: true);
                e.Handled = true;
                break;
        }
    }

    private async Task ChooseAsync(bool left)
    {
        if (_busy || _session is null || _exiting || !BothPanesReady())
            return;
        _busy = true;
        try
        {
            await PlaySelectCueAsync(left);
            if (_exiting)
                return;
            if (left) _session.VoteLeft();
            else _session.VoteRight();
            ShowCurrent();
            AnimateLastMatchBall();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
        finally
        {
            ResetPaneTransforms();
            _busy = false;
        }
    }

    private Task PlaySelectCueAsync(bool left)
    {
        StopSelectCue();
        var winScale = left ? LeftScale : RightScale;
        var flash = left ? LeftFlash : RightFlash;
        var ring = left ? LeftRing : RightRing;
        var done = new TaskCompletionSource();

        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var story = new Storyboard();

        story.Children.Add(Anim(flash, UIElement.OpacityProperty, 0, 0.22, 35, easeOut));
        story.Children.Add(Anim(flash, UIElement.OpacityProperty, 0.22, 0, 65, easeIn, beginMs: 35));
        story.Children.Add(Anim(ring, UIElement.OpacityProperty, 0, 1, 40, easeOut));
        story.Children.Add(Anim(ring, UIElement.OpacityProperty, 1, 0, 60, easeIn, beginMs: 40));
        story.Children.Add(Anim(winScale, ScaleTransform.ScaleXProperty, 1, 1.03, 45, easeOut));
        story.Children.Add(Anim(winScale, ScaleTransform.ScaleYProperty, 1, 1.03, 45, easeOut));
        story.Children.Add(Anim(winScale, ScaleTransform.ScaleXProperty, 1.03, 1, 55, easeIn, beginMs: 45));
        story.Children.Add(Anim(winScale, ScaleTransform.ScaleYProperty, 1.03, 1, 55, easeIn, beginMs: 45));

        story.Completed += (_, _) => done.TrySetResult();
        _selectCue = story;
        story.Begin();
        return done.Task;
    }

    private static DoubleAnimation Anim(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        int ms,
        IEasingFunction ease,
        int beginMs = 0)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(property));
        return anim;
    }

    private void StopSelectCue()
    {
        if (_selectCue is null)
            return;
        _selectCue.Stop();
        _selectCue.Remove();
        _selectCue = null;
        ResetPaneTransforms();
    }

    private void ResetPaneTransforms()
    {
        LeftScale.ScaleX = LeftScale.ScaleY = 1;
        RightScale.ScaleX = RightScale.ScaleY = 1;
        LeftPane.Opacity = 1;
        RightPane.Opacity = 1;
        LeftFlash.Opacity = 0;
        RightFlash.Opacity = 0;
        LeftRing.Opacity = 0;
        RightRing.Opacity = 0;
    }

    private void Skip()
    {
        if (_busy || _session is null || !BothPanesReady())
            return;
        _busy = true;
        try
        {
            try
            {
                _session.Skip();
                ShowCurrent();
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message);
            }
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
        _leftVideo.SetPanelSize(w, h);
        _rightVideo.SetPanelSize(w, h);
        _pipeline.SetPanelPixelSize(w, h);
    }
}
