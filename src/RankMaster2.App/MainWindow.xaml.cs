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
    private bool _suppressMediaFailed;
    private bool _inShowCurrent;
    private readonly VideoSlot _leftSlot = new();
    private readonly VideoSlot _rightSlot = new();
    private Storyboard? _selectCue;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _loadTimer;

    private sealed class VideoSlot
    {
        public int Generation;
        public bool Opened;
        public string? ExpectedPath;
    }

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
        DebugLog.DiskSavesEnabled = false;
        DebugLog.Write("==== app start (disk ranking saves OFF) ====");
        ApplyExclusiveFullscreen();
        RefreshResumeButton();
        _loadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _loadTimer.Tick += (_, _) => RefreshLoadProgress();
        Closed += (_, _) =>
        {
            _loadTimer.Stop();
            _pipeline.Dispose();
        };
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
        _suppressMediaFailed = true;
        StopVideo(LeftVideo);
        StopVideo(RightVideo);
        LeftImage.Source = null;
        RightImage.Source = null;
        LeftLoad.Visibility = Visibility.Collapsed;
        RightLoad.Visibility = Visibility.Collapsed;
        _loadTimer?.Stop();
        try { _pipeline.ReleaseAll(); } catch { /* disposed */ }
        if (!_inShowCurrent)
            _suppressMediaFailed = false;
    }

    private void SetStartError(string message)
    {
        StartError.Text = message;
        StartErrorBox.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowCurrent()
    {
        if (_inShowCurrent)
        {
            DebugLog.Write("ShowCurrent REENTER ignored");
            return;
        }
        _inShowCurrent = true;
        _suppressMediaFailed = true;
        DebugLog.Write($"ShowCurrent ENTER current={_session?.Current} unranked={_session?.UnrankedCount} suppress=1");
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
            ResetPane(LeftImage, LeftVideo, LeftLoad, LeftLoadPct, LeftName, pair.Left);
            ResetPane(RightImage, RightVideo, RightLoad, RightLoadPct, RightName, pair.Right);
            _loadTimer?.Start();
            OverlayFolder.Text = _session.FolderName;
            OverlayUnranked.Text = _session.UnrankedCount.ToString("N0");
            OverlaySession.Text = _session.SessionVotes.ToString("N0");
            var progress = LibraryProgress.Of(_session.Rankable);
            OverlayConfidencePct.Text = $"{Math.Round(progress * 100)}%";
            ConfidenceFill.Width = 232 * progress;

            DebugLog.Write($"ShowCurrent pair={pair.Left.Filename}|{pair.Right.Filename}");
            _pipeline.Show(pair.Left, pair.Right);
            foreach (var warm in _session.WarmPairs)
                _pipeline.Enqueue(warm);
        }
        finally
        {
            _inShowCurrent = false;
            Dispatcher.BeginInvoke(() =>
            {
                _suppressMediaFailed = false;
                DebugLog.Write("ShowCurrent suppress->0 (Background)");
            }, DispatcherPriority.Background);
        }
    }

    private void ResetPane(
        System.Windows.Controls.Image image,
        MediaElement video,
        UIElement load,
        TextBlock loadPct,
        TextBlock name,
        MediaId id)
    {
        StopVideo(video);
        image.Source = null;
        name.Text = id.Filename;
        loadPct.Text = "0%";
        load.Visibility = Visibility.Visible;
    }

    private void OnFrameReady(MediaId id, PreparedFrame frame)
    {
        DebugLog.Write($"OnFrameReady {id.Filename} kind={frame.Kind} preview={frame.IsPreview} current={_session?.Current}");
        if (_session?.Current is null)
            return;
        var pair = _session.Current.Value;
        if (pair.Left == id)
            ApplyFrame(LeftImage, LeftVideo, LeftLoad, frame);
        else if (pair.Right == id)
            ApplyFrame(RightImage, RightVideo, RightLoad, frame);
    }

    private void OnFrameFailed(MediaId id)
    {
        DebugLog.Write($"OnFrameFailed {id.Filename} busy={_busy} inShow={_inShowCurrent} suppress={_suppressMediaFailed} current={_session?.Current}");
        if (_session is null || _busy || _inShowCurrent)
        {
            DebugLog.Write($"OnFrameFailed IGNORE {id.Filename}");
            return;
        }
        if (_session.Current is not { } pair || !pair.Contains(id))
        {
            DebugLog.Write($"OnFrameFailed IGNORE not-in-current {id.Filename}");
            return;
        }
        DebugLog.Write($"OnFrameFailed DROP {id.Filename}");
        _session.Drop(id);
        ShowCurrent();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var side = sender == LeftVideo ? "L" : sender == RightVideo ? "R" : "?";
        var err = e.ErrorException;
        var failedEl = sender as MediaElement;
        DebugLog.Write($"OnMediaFailed {side} suppress={_suppressMediaFailed} inShow={_inShowCurrent} src={(failedEl is null ? "" : SourcePath(failedEl))} expected={SlotOfOrNull(failedEl)?.ExpectedPath} err={err?.GetType().Name}:{err?.Message}");
        if (_suppressMediaFailed || _inShowCurrent || _exiting)
            return;
        if (RankPanel.Visibility != Visibility.Visible)
            return;
        if (sender is not MediaElement el)
            return;
        var slot = SlotOf(el);
        if (slot.ExpectedPath is null || el.Source is null)
        {
            DebugLog.Write($"OnMediaFailed {side} IGNORE src/expected null");
            return;
        }
        if (!PathsMatch(SourcePath(el), slot.ExpectedPath))
        {
            DebugLog.Write($"OnMediaFailed {side} IGNORE path mismatch");
            return;
        }
        var id = new MediaId(Path.GetFileName(slot.ExpectedPath));
        OnFrameFailed(id);
    }

    private VideoSlot? SlotOfOrNull(MediaElement? video) =>
        video is null ? null : SlotOf(video);

    private void ApplyFrame(
        System.Windows.Controls.Image image,
        MediaElement video,
        UIElement load,
        PreparedFrame frame)
    {
        if (frame.Kind == MediaKind.Video)
        {
            var slot = SlotOf(video);
            if (slot.Opened && PathsMatch(SourcePath(video), frame.VideoPath))
                return;
            StopVideo(video);
            if (frame.VideoPath is null)
                return;
            var gen = slot.Generation;
            var path = frame.VideoPath;
            slot.ExpectedPath = path;
            load.Visibility = Visibility.Visible;
            DebugLog.Write($"ApplyFrame video {path} gen={gen} queue Source+Play");
            // Assign Source after Normal-priority MediaFailed from Close() has run.
            // Manual LoadedBehavior does not open until Play() is called.
            Dispatcher.BeginInvoke(() =>
            {
                if (slot.Generation != gen || slot.ExpectedPath != path)
                {
                    DebugLog.Write($"ApplyFrame ABORT stale gen={slot.Generation}/{gen} {path}");
                    return;
                }
                if (_session?.Current is not { } pair || !pair.Contains(new MediaId(Path.GetFileName(path))))
                {
                    DebugLog.Write($"ApplyFrame ABORT not current {path}");
                    return;
                }
                DebugLog.Write($"ApplyFrame SET Source+Play {path}");
                video.Source = new Uri(Path.GetFullPath(path));
                try { video.Play(); } catch (Exception ex) { DebugLog.Write($"Play threw {ex.Message}"); }
            }, DispatcherPriority.Background);
            return;
        }

        StopVideo(video);
        image.Source = frame.Still;
        load.Visibility = Visibility.Collapsed;
    }

    private VideoSlot SlotOf(MediaElement video) =>
        video == LeftVideo ? _leftSlot : _rightSlot;

    private static string? SourcePath(MediaElement video)
    {
        if (video.Source is null)
            return null;
        try { return video.Source.LocalPath; }
        catch (InvalidOperationException) { return video.Source.OriginalString; }
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

    private void StopVideo(MediaElement video)
    {
        var slot = SlotOf(video);
        slot.Generation++;
        slot.Opened = false;
        slot.ExpectedPath = null;
        DebugLog.Write($"StopVideo gen={slot.Generation} hadSrc={video.Source}");
        var prior = _suppressMediaFailed;
        _suppressMediaFailed = true;
        try
        {
            try { video.Stop(); } catch { /* not opened */ }
            // Do not Close() — after Close a MediaElement often never opens again.
            video.Source = null;
        }
        finally
        {
            _suppressMediaFailed = prior;
        }
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        if (RankPanel.Visibility != Visibility.Visible || _exiting)
            return;
        if (sender is not MediaElement el)
            return;
        var slot = SlotOf(el);
        if (!slot.Opened || !PathsMatch(SourcePath(el), slot.ExpectedPath))
            return;
        el.Position = TimeSpan.Zero;
        el.Play();
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (RankPanel.Visibility != Visibility.Visible || _exiting)
            return;
        if (sender is not MediaElement el)
            return;
        var slot = SlotOf(el);
        if (slot.ExpectedPath is null)
        {
            DebugLog.Write($"OnVideoOpened IGNORE no expected src={SourcePath(el)}");
            return;
        }
        if (!PathsMatch(SourcePath(el), slot.ExpectedPath))
        {
            DebugLog.Write($"OnVideoOpened IGNORE mismatch src={SourcePath(el)} expected={slot.ExpectedPath}");
            return;
        }
        slot.Opened = true;
        DebugLog.Write($"OnVideoOpened OK {slot.ExpectedPath}");
        if (sender == LeftVideo)
            LeftLoad.Visibility = Visibility.Collapsed;
        if (sender == RightVideo)
            RightLoad.Visibility = Visibility.Collapsed;
        el.Volume = 0;
        el.IsMuted = true;
        try { el.Play(); } catch { /* already playing */ }
    }

    private bool BothPanesReady()
    {
        if (_session?.Current is not { } pair)
            return false;
        return PaneReady(pair.Left, LeftImage, _leftSlot) &&
               PaneReady(pair.Right, RightImage, _rightSlot);
    }

    private static bool PaneReady(MediaId id, System.Windows.Controls.Image image, VideoSlot slot)
    {
        var kind = MediaExtensions.KindOf(id.Filename);
        if (kind == MediaKind.Video)
            return slot.Opened;
        return image.Source is not null;
    }

    private void RefreshLoadProgress()
    {
        if (RankPanel.Visibility != Visibility.Visible)
        {
            _loadTimer?.Stop();
            return;
        }

        UpdateLoadChrome(LeftVideo, LeftLoad, LeftLoadPct, _leftSlot, LeftImage);
        UpdateLoadChrome(RightVideo, RightLoad, RightLoadPct, _rightSlot, RightImage);
        if (BothPanesReady())
            _loadTimer?.Stop();
    }

    private static void UpdateLoadChrome(
        MediaElement video,
        UIElement load,
        TextBlock pct,
        VideoSlot slot,
        System.Windows.Controls.Image image)
    {
        if (load.Visibility != Visibility.Visible)
            return;
        if (slot.Opened || image.Source is not null)
        {
            load.Visibility = Visibility.Collapsed;
            return;
        }

        double progress = 0;
        if (video.Source is not null)
        {
            try
            {
                progress = Math.Max(video.DownloadProgress, video.BufferingProgress);
            }
            catch (InvalidOperationException)
            {
                progress = 0;
            }
        }

        pct.Text = $"{Math.Clamp((int)Math.Round(progress * 100), 0, 99)}%";
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
        {
            DebugLog.Write($"Choose IGNORE busy={_busy} ready={BothPanesReady()} exiting={_exiting}");
            return;
        }
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
        _pipeline.SetPanelPixelSize(w, h);
    }
}
