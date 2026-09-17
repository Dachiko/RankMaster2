using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Video;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// One side of the compare screen (plan § 1.2, § 4.3): the frame, the select cue, the wait ring,
/// the pane sentence, the filename. The only control that touches pixels: it copies a still's raw
/// BGRA buffer into a <see cref="WriteableBitmap"/> once per <see cref="PaneState"/> instance (never
/// re-copying the same one twice) and disposes the <see cref="StillLease"/> immediately after, per
/// its contract ("MUST be disposed once its bitmap copy is made"). A video pane instead reads the
/// live <see cref="IVideoSurface.Frame"/> straight through -- that bitmap is D's, not copied here.
/// </summary>
public partial class PaneControl : UserControl
{
    private readonly Image _frame;
    private readonly Border _flashOverlay;
    private readonly Border _cueRing;
    private readonly ScaleTransform _cueScale;
    private readonly StackPanel _waitPanel;
    private readonly TextBlock _waitPercent;
    private readonly TextBlock _engineWakeLine;
    private readonly TextBlock _sentence;
    private readonly TextBlock _filename;

    private PaneState? _lastRenderedPane;

    public PaneControl()
    {
        AvaloniaXamlLoader.Load(this);
        _frame = this.FindControl<Image>("Frame")!;
        _flashOverlay = this.FindControl<Border>("FlashOverlay")!;
        _cueRing = this.FindControl<Border>("CueRing")!;
        _cueScale = new ScaleTransform(1, 1);
        _cueRing.RenderTransform = _cueScale;
        _waitPanel = this.FindControl<StackPanel>("WaitPanel")!;
        _waitPercent = this.FindControl<TextBlock>("WaitPercent")!;
        _engineWakeLine = this.FindControl<TextBlock>("EngineWakeLine")!;
        _sentence = this.FindControl<TextBlock>("Sentence")!;
        _filename = this.FindControl<TextBlock>("Filename")!;
    }

    public void Render(PaneState pane, Side side, IVideoSurface? videoSurface, VideoEngineStatus engineStatus, IClock clock)
    {
        _filename.Text = pane.Id;
        if (side == Side.Left)
        {
            _filename.HorizontalAlignment = HorizontalAlignment.Left;
            _filename.Margin = new Thickness(280, 18, 0, 0);
        }
        else
        {
            _filename.HorizontalAlignment = HorizontalAlignment.Right;
            _filename.Margin = new Thickness(0, 18, 64, 0);
        }

        var showsSentence = pane.Kind is PaneKind.Gone or PaneKind.Undecodable or PaneKind.NoVideoEngine;
        _sentence.IsVisible = showsSentence;
        _sentence.Text = pane.Sentence;

        var showsPixels = pane.Kind is PaneKind.Ready or PaneKind.Refining;
        _frame.IsVisible = showsPixels;

        if (!ReferenceEquals(pane, _lastRenderedPane))
        {
            _lastRenderedPane = pane;
            if (showsPixels && !pane.IsVideo && pane.Lease is { } lease)
            {
                _frame.Source = CopyToBitmap(lease.Frame);
                lease.Dispose(); // contract: disposed once its bitmap copy is made
            }
        }

        if (showsPixels && pane.IsVideo)
            _frame.Source = videoSurface?.Frame;

        var waiting = pane.Kind == PaneKind.Waiting;
        if (pane.IsVideo)
        {
            // D exposes no buffer percentage (see the report's § 2.3 reconciliation) -- a plain
            // indeterminate ring is shown at once for video, per plan § 3.6.
            _waitPanel.IsVisible = waiting;
            _waitPercent.IsVisible = false;
            _engineWakeLine.IsVisible = waiting && engineStatus == VideoEngineStatus.Starting
                && clock.UtcNow - pane.WaitingSince >= Timings.EngineWakeNoticeAfterMs;
        }
        else
        {
            _engineWakeLine.IsVisible = false;
            _waitPercent.IsVisible = false;
            _waitPanel.IsVisible = waiting && clock.UtcNow - pane.WaitingSince >= Timings.StillRingGraceMs;
        }
    }

    // ---- select cue (plan § 1.2, timings to the millisecond) --------------------------------------

    public void ResetCue()
    {
        _flashOverlay.Opacity = 0;
        _cueRing.Opacity = 0;
        _cueScale.ScaleX = 1;
        _cueScale.ScaleY = 1;
    }

    /// <summary>Plays the ~100 ms select cue and returns a task that completes when every animated
    /// value is back at rest -- explicit reset, so nothing sticks (plan § 1.2). Uses
    /// <c>Avalonia.Animation.Animation.RunAsync</c> directly (no <c>Styles</c>, no XAML-declared
    /// animation) because which of the two panes plays it is only known at press time.</summary>
    public async Task PlayCue()
    {
        var flash = BuildAnimation(Border.OpacityProperty, (0, 0.0), (0.35, 0.22), (1.0, 0.0)).RunAsync(_flashOverlay);
        var ring = BuildAnimation(Border.OpacityProperty, (0, 0.0), (0.4, 1.0), (1.0, 0.0)).RunAsync(_cueRing);
        var scaleX = BuildAnimation(ScaleTransform.ScaleXProperty, (0, 1.0), (0.45, 1.03), (1.0, 1.0)).RunAsync(_cueScale);
        var scaleY = BuildAnimation(ScaleTransform.ScaleYProperty, (0, 1.0), (0.45, 1.03), (1.0, 1.0)).RunAsync(_cueScale);

        await Task.WhenAll(flash, ring, scaleX, scaleY);
        ResetCue(); // FillMode.None already leaves nothing set, but this is the explicit belt-and-braces reset plan § 1.2 asks for.
    }

    private static Animation BuildAnimation(AvaloniaProperty property, params (double Cue, double Value)[] keyframes)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(100),
            FillMode = FillMode.None,
        };
        foreach (var (cue, value) in keyframes)
            animation.Children.Add(new KeyFrame { Cue = new Cue(cue), Setters = { new Setter(property, value) } });
        return animation;
    }

    private static unsafe WriteableBitmap CopyToBitmap(StillFrame frame)
    {
        var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();

        var src = (byte*)frame.Pixels;
        var dst = (byte*)fb.Address;
        var rowBytes = Math.Min(frame.RowBytes, fb.RowBytes);
        for (var y = 0; y < frame.Height; y++)
            Buffer.MemoryCopy(src + (long)y * frame.RowBytes, dst + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);

        return bitmap;
    }
}
