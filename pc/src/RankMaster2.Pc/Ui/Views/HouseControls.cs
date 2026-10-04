using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// The ink card's faint dot grid (plan H § 3.2: "faint 7 px dot grid"; the mockup's
/// <c>radial-gradient(rgba(255,255,255,.07) 1px, transparent 1.2px)</c> on a 7 px tile). Avalonia has
/// no radial-gradient brush and a tiled <c>DrawingBrush</c> cannot be checked without opening a
/// window, so the dots are drawn directly: one small ellipse per 7 px cell, centred in it. Not
/// hit-testable and not focusable; it only sits behind the card's content.
/// </summary>
public sealed class DotGrid : Control
{
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<DotGrid, IBrush?>(nameof(Brush));

    public const double Pitch = 7;
    public const double Radius = 0.9;

    static DotGrid()
    {
        AffectsRender<DotGrid>(BrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DotGrid>(false);
        FocusableProperty.OverrideDefaultValue<DotGrid>(false);
    }

    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    /// <summary>How many dots fit on a surface of this size: whole cells only, one dot per cell.</summary>
    public static (int Columns, int Rows) CellsFor(Size size) =>
        ((int)Math.Floor(size.Width / Pitch), (int)Math.Floor(size.Height / Pitch));

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush) return;
        var (columns, rows) = CellsFor(Bounds.Size);
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                context.DrawEllipse(brush, null, new Point((column + 0.5) * Pitch, (row + 0.5) * Pitch), Radius, Radius);
    }
}

/// <summary>
/// DESIGN.md § 3 "dotted rule": a 1.5 px dotted line across the available width (the mockup's
/// <c>border-top: 1.5px dotted</c>). Avalonia's <c>Line</c> needs fixed end points and a <c>Border</c>
/// cannot dash, so the dots are drawn directly: 1.5 px round dots, 3 px apart.
/// </summary>
public sealed class DottedRule : Control
{
    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<DottedRule, IBrush?>(nameof(Brush));

    public const double Thickness = 1.5;
    public const double Period = 3;

    static DottedRule()
    {
        AffectsRender<DottedRule>(BrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DottedRule>(false);
        FocusableProperty.OverrideDefaultValue<DottedRule>(false);
    }

    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Thickness);

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush) return;
        for (var x = Thickness / 2; x <= Bounds.Width - Thickness / 2; x += Period)
            context.DrawEllipse(brush, null, new Point(x, Thickness / 2), Thickness / 2, Thickness / 2);
    }
}

/// <summary>
/// Plan H § 3.2's row of 60 dots that fill red from the left with the percent (the mockup's
/// <c>.dots</c>: 6 px dots, 4 px apart, unfilled <c>#3A3A3A</c>). On a card narrower than 60 dots
/// need, the dots shrink (the mockup's flex row did the same) rather than overflow.
/// <see cref="Filled"/> is a number of dots, not a percent, so <see cref="DotsFor"/> is the one place
/// the percent turns into dots and a test can pin it.
/// </summary>
public sealed class DotRow : Control
{
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<DotRow, int>(nameof(Count), 60);
    public static readonly StyledProperty<double> FilledProperty = AvaloniaProperty.Register<DotRow, double>(nameof(Filled));
    public static readonly StyledProperty<IBrush?> OnBrushProperty = AvaloniaProperty.Register<DotRow, IBrush?>(nameof(OnBrush));
    public static readonly StyledProperty<IBrush?> OffBrushProperty = AvaloniaProperty.Register<DotRow, IBrush?>(nameof(OffBrush));

    public const double DotSize = 6;
    public const double Gap = 4;

    static DotRow()
    {
        AffectsRender<DotRow>(CountProperty, FilledProperty, OnBrushProperty, OffBrushProperty);
        AffectsMeasure<DotRow>(CountProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DotRow>(false);
        FocusableProperty.OverrideDefaultValue<DotRow>(false);
    }

    public int Count { get => GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public double Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public IBrush? OnBrush { get => GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public IBrush? OffBrush { get => GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    /// <summary>Percent (0..100) → lit dots out of <paramref name="count"/>, rounded to the nearest
    /// dot and clamped. 37 % of 60 is 22, the mockup's own example.</summary>
    public static int DotsFor(int percent, int count) =>
        Math.Clamp((int)Math.Round(Math.Clamp(percent, 0, 100) * count / 100.0, MidpointRounding.AwayFromZero), 0, count);

    /// <summary>The diameter that makes <paramref name="count"/> dots with <see cref="Gap"/> between
    /// them fit in <paramref name="width"/>, never larger than <see cref="DotSize"/>.</summary>
    public static double DiameterFor(double width, int count) =>
        count <= 0 || double.IsInfinity(width) ? DotSize : Math.Clamp((width - Gap * (count - 1)) / count, 1, DotSize);

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = Count * DotSize + Math.Max(0, Count - 1) * Gap;
        var width = double.IsInfinity(availableSize.Width) ? natural : Math.Min(natural, availableSize.Width);
        return new Size(width, DotSize);
    }

    public override void Render(DrawingContext context)
    {
        var count = Count;
        if (count <= 0) return;
        var diameter = DiameterFor(Bounds.Width, count);
        var pitch = count > 1 ? (Bounds.Width - diameter) / (count - 1) : 0;
        var lit = (int)Math.Round(Filled, MidpointRounding.AwayFromZero);
        for (var i = 0; i < count; i++)
        {
            var brush = i < lit ? OnBrush : OffBrush;
            if (brush is null) continue;
            var centre = new Point(diameter / 2 + i * pitch, Bounds.Height / 2);
            context.DrawEllipse(brush, null, centre, diameter / 2, diameter / 2);
        }
    }
}

/// <summary>
/// DESIGN.md § 3 "busy line": 2 px high, a 1 px <c>line</c> hairline with a red dash (30 % of the width,
/// at least 18 px) sweeping to and fro, ~0.9 s a way. Work under way shows motion, not a sentence. It runs
/// only while it is on screen (so a hidden one costs nothing) and stops on its own when it leaves it.
/// The sweep is a <see cref="Phase"/> animated 0..1 and back; nothing else about it moves.
/// </summary>
public sealed class BusyLine : Control
{
    public static readonly StyledProperty<double> PhaseProperty = AvaloniaProperty.Register<BusyLine, double>(nameof(Phase));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<BusyLine, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> DashBrushProperty = AvaloniaProperty.Register<BusyLine, IBrush?>(nameof(DashBrush));

    public static readonly TimeSpan SweepDuration = TimeSpan.FromMilliseconds(900);

    private CancellationTokenSource? _running;

    static BusyLine()
    {
        AffectsRender<BusyLine>(PhaseProperty, TrackBrushProperty, DashBrushProperty);
        ClipToBoundsProperty.OverrideDefaultValue<BusyLine>(true);
        IsHitTestVisibleProperty.OverrideDefaultValue<BusyLine>(false);
        FocusableProperty.OverrideDefaultValue<BusyLine>(false);
    }

    public double Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? DashBrush { get => GetValue(DashBrushProperty); set => SetValue(DashBrushProperty, value); }

    /// <summary>The dash's left edge and width for a phase and a track width (CSS: left from -30 % to
    /// 100 %). Pure, so a test can pin it.</summary>
    public static (double Left, double Width) DashFor(double phase, double trackWidth)
    {
        var width = Math.Max(18, trackWidth * 0.3);
        var left = -0.3 * trackWidth + Math.Clamp(phase, 0, 1) * 1.3 * trackWidth;
        return (left, width);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 96 : availableSize.Width, 2);

    public override void Render(DrawingContext context)
    {
        if (TrackBrush is { } track) context.DrawRectangle(track, null, new Rect(0, 0.5, Bounds.Width, 1));
        if (DashBrush is { } dash)
        {
            var (left, width) = DashFor(Phase, Bounds.Width);
            context.DrawRectangle(dash, null, new Rect(left, 0, width, 2));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateRunning();
    }

    private void UpdateRunning()
    {
        if (IsEffectivelyVisible && this.GetVisualRoot() is not null) Start();
        else Stop();
    }

    private void Start()
    {
        if (_running is not null) return;
        _running = new CancellationTokenSource();
        var sweep = new Animation
        {
            Duration = SweepDuration,
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(PhaseProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(PhaseProperty, 1.0) } },
            },
        };
        _ = sweep.RunAsync(this, _running.Token);
    }

    private void Stop()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }
}
