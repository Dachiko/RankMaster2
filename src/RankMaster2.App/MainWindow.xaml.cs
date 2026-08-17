using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private Storyboard? _selectCue;
    private DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();
        _pipeline = CreatePipeline();
        _actions = new LibraryActions(
            folder: () => _session?.Folder ?? LastFolderStore.Load() ?? "",
            catalog: _catalog,
            pipeline: () => _pipeline,
            session: () => _session,
            releaseUi: ReleaseUi);
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
        StopVideo(LeftVideo);
        StopVideo(RightVideo);
        LeftImage.Source = null;
        RightImage.Source = null;
        try { _pipeline.ReleaseAll(); } catch { /* disposed */ }
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
            LeaveCompare();
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
        var progress = LibraryProgress.Of(_session.Rankable);
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
        if (_session is null || _busy)
            return;
        if (_session.Current is not { } pair || !pair.Contains(id))
            return;
        _session.Drop(id);
        ShowCurrent();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_session?.Current is not { } pair)
            return;
        if (sender == LeftVideo)
            OnFrameFailed(pair.Left);
        else if (sender == RightVideo)
            OnFrameFailed(pair.Right);
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
        try { video.Close(); } catch { /* not opened */ }
        video.Source = null;
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        if (RankPanel.Visibility != Visibility.Visible)
            return;
        if (sender is MediaElement el && el.Source is not null)
        {
            el.Position = TimeSpan.Zero;
            el.Play();
        }
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (RankPanel.Visibility != Visibility.Visible)
            return;
        if (sender == LeftVideo)
            LeftSpinner.Visibility = Visibility.Collapsed;
        if (sender == RightVideo)
            RightSpinner.Visibility = Visibility.Collapsed;
        if (sender is MediaElement el && el.Source is not null)
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
        if (_busy || _session?.Current is null)
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
            StopVideo(LeftVideo);
            LeftImage.Source = null;
        }
        if (pair.Right == id)
        {
            StopVideo(RightVideo);
            RightImage.Source = null;
        }
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
            StopSelectCue();
            Application.Current.Shutdown();
            return;
        }

        if (e.Key == Key.O)
        {
            e.Handled = true;
            if (!_renaming)
                PickFolder();
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

        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            UndoMove();
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
        if (_busy || _session is null || _exiting)
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
        if (_busy || _session is null)
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
        _pipeline.SetPanelPixelSize(w, h);
    }
}
