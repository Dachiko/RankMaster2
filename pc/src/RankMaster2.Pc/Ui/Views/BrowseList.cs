using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// The browser's rows (plan I § 2.2 items 3, 5, 7; mockup F): folder names only, Inter Medium 16, drawn directly
/// rather than as an items control so that rows are always <b>whole</b> (the list is as tall as a whole number of
/// rows; no scrollbar, no half row), the selected row can be filled ink, rows can glide to a new order when
/// letters are typed (DESIGN.md "Filtering reorders", ~180 ms), and a folder with thousands of children costs the
/// same as one with ten (only the rows in view are drawn).
/// <para/>
/// Selected row: ink fill, on-ink text. Under the mouse: <c>paper</c>. Dimmed rows (not openable / not available /
/// not readable) and rows that do not match the typed letters: ≈ 35 % (75 % when selected). A match is underlined in
/// the accent. Nothing here decides anything: a click reports its row, <see cref="BrowseView"/> hands it on.
/// Colours come from the palette through the four brush properties.
/// </summary>
public sealed class BrowseList : Control
{
    public static readonly StyledProperty<IBrush?> InkBrushProperty = AvaloniaProperty.Register<BrowseList, IBrush?>(nameof(InkBrush));
    public static readonly StyledProperty<IBrush?> OnInkBrushProperty = AvaloniaProperty.Register<BrowseList, IBrush?>(nameof(OnInkBrush));
    public static readonly StyledProperty<IBrush?> PaperBrushProperty = AvaloniaProperty.Register<BrowseList, IBrush?>(nameof(PaperBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<BrowseList, IBrush?>(nameof(AccentBrush));

    /// <summary>The mockup's row: 16 px text, 9 px padding above and below.</summary>
    public const double RowHeight = 38;
    public const double TextInset = 12;
    public const double DimmedOpacity = 0.35;
    public const double DimmedSelectedOpacity = 0.75;
    public const double FontSize = 16;
    public static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(180);
    private const int WheelRows = 3;

    private IReadOnlyList<BrowseRow> _rows = [];
    private int _selected = BrowseModel.Here;
    private int _hover = -1;
    private int _first;
    private Dictionary<string, double> _glideFrom = [];
    private Stopwatch? _glideWatch;
    private DispatcherTimer? _glideTimer;

    /// <summary>A click on a row (its index in <see cref="BrowseModel.Rows"/>).</summary>
    public event Action<int>? RowClicked;

    /// <summary>The second click of a double-click: Enter on that row.</summary>
    public event Action<int>? RowActivated;

    static BrowseList()
    {
        AffectsRender<BrowseList>(InkBrushProperty, OnInkBrushProperty, PaperBrushProperty, AccentBrushProperty);
        ClipToBoundsProperty.OverrideDefaultValue<BrowseList>(true);
        FocusableProperty.OverrideDefaultValue<BrowseList>(false);
    }

    public IBrush? InkBrush { get => GetValue(InkBrushProperty); set => SetValue(InkBrushProperty, value); }
    public IBrush? OnInkBrush { get => GetValue(OnInkBrushProperty); set => SetValue(OnInkBrushProperty, value); }
    public IBrush? PaperBrush { get => GetValue(PaperBrushProperty); set => SetValue(PaperBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>How many whole rows fit; the model's page size.</summary>
    public int Fit => Math.Max(1, (int)Math.Floor(Bounds.Height / RowHeight));

    /// <summary>The first row in view.</summary>
    public int FirstRow => _first;

    /// <summary>
    /// Shows <paramref name="rows"/> with <paramref name="selected"/> selected. <paramref name="newListing"/> (a listing or
    /// the roots just landed): the rows take their place at once, the selection is scrolled to the middle, nothing glides.
    /// Otherwise (typing) rows that kept their folder glide from where they were to where they are. The list always
    /// keeps the selection in view.
    /// </summary>
    public void Sync(IReadOnlyList<BrowseRow> rows, int selected, bool newListing)
    {
        var old = _rows;
        var moved = selected != _selected || !ReferenceEquals(old, rows);
        _rows = rows;
        _selected = selected;

        if (newListing)
        {
            StopGlide();
            _first = selected > 0 ? selected - Fit / 2 : 0;
        }
        else if (!ReferenceEquals(old, rows) && old.Count == rows.Count && rows.Count > 0)
        {
            StartGlide(old, rows);
        }

        _hover = Math.Min(_hover, rows.Count - 1);
        // Only when the selection or the rows changed: a repaint for another reason must not undo a wheel scroll.
        if (moved) KeepSelectionInView();
        InvalidateVisual();
    }

    private void KeepSelectionInView()
    {
        var fit = Fit;
        if (_selected < 0) _first = 0; // here is above row 0
        else if (_selected < _first) _first = _selected;
        else if (_selected >= _first + fit) _first = _selected - fit + 1;
        _first = Math.Clamp(_first, 0, Math.Max(0, _rows.Count - fit));
    }

    // ---- glide ------------------------------------------------------------------------------------------------------

    private void StartGlide(IReadOnlyList<BrowseRow> old, IReadOnlyList<BrowseRow> rows)
    {
        var before = new Dictionary<string, int>(old.Count);
        for (var i = 0; i < old.Count; i++) before[old[i].Path] = i;

        var from = new Dictionary<string, double>();
        for (var i = 0; i < rows.Count; i++)
            if (before.TryGetValue(rows[i].Path, out var was) && was != i)
                from[rows[i].Path] = (was - i) * RowHeight;
        if (from.Count == 0) return;

        _glideFrom = from;
        _glideWatch = Stopwatch.StartNew();
        _glideTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => GlideTick());
        _glideTimer.Start();
    }

    private void GlideTick()
    {
        if (_glideWatch is null || _glideWatch.Elapsed >= GlideDuration) StopGlide();
        InvalidateVisual();
    }

    private void StopGlide()
    {
        _glideTimer?.Stop();
        _glideWatch = null;
        _glideFrom = [];
    }

    /// <summary>1 → 0 over <see cref="GlideDuration"/>, ease-out: how much of the row's old offset is left.</summary>
    private double GlideLeft()
    {
        if (_glideWatch is null) return 0;
        var t = Math.Clamp(_glideWatch.Elapsed / GlideDuration, 0, 1);
        return Math.Pow(1 - t, 3);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopGlide();
        base.OnDetachedFromVisualTree(e);
    }

    // ---- drawing ------------------------------------------------------------------------------------------------------

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        KeepSelectionInView();
    }

    public override void Render(DrawingContext context)
    {
        // A transparent fill, so the whole area takes the pointer (rows, and the space under them).
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (_rows.Count == 0) return;

        var fit = Fit;
        using var clip = context.PushClip(new Rect(0, 0, Bounds.Width, fit * RowHeight));
        var left = GlideLeft();
        var face = new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Medium);

        for (var i = Math.Max(0, _first - 3); i < Math.Min(_rows.Count, _first + fit + 3); i++)
        {
            var row = _rows[i];
            var y = (i - _first) * RowHeight;
            if (left > 0 && _glideFrom.TryGetValue(row.Path, out var offset)) y += offset * left;
            if (y + RowHeight <= 0 || y >= fit * RowHeight) continue;

            var selected = i == _selected;
            var rect = new Rect(0, y, Bounds.Width, RowHeight);
            if (selected) context.DrawRectangle(InkBrush, null, rect, 6, 6);
            else if (i == _hover) context.DrawRectangle(PaperBrush, null, rect, 6, 6);

            var dim = row.Dimmed || row.Far;
            using var fade = context.PushOpacity(dim ? (selected ? DimmedSelectedOpacity : DimmedOpacity) : 1);
            var text = new FormattedText(row.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize,
                selected ? OnInkBrush : InkBrush)
            {
                MaxTextWidth = Math.Max(1, Bounds.Width - TextInset * 2),
                MaxTextHeight = RowHeight,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var origin = new Point(TextInset, y + (RowHeight - text.Height) / 2);
            context.DrawText(text, origin);

            if (!row.Far && row.MatchLength > 0 && row.MatchStart + row.MatchLength <= row.Name.Length
                && text.BuildHighlightGeometry(origin, row.MatchStart, row.MatchLength) is { } box)
                context.DrawRectangle(AccentBrush, null, new Rect(box.Bounds.Left, box.Bounds.Bottom - 1.5, box.Bounds.Width, 1.5));
        }
    }

    // ---- mouse (plan I § 2.2 item 7) ------------------------------------------------------------------------------------

    private int RowAt(Point point)
    {
        if (point.Y < 0 || point.Y >= Fit * RowHeight) return -1;
        var index = _first + (int)(point.Y / RowHeight);
        return index < _rows.Count ? index : -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var index = RowAt(e.GetPosition(this));
        if (index < 0) return;
        e.Handled = true;
        if (e.ClickCount >= 2) RowActivated?.Invoke(index);
        else RowClicked?.Invoke(index);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = RowAt(e.GetPosition(this));
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    /// <summary>The wheel scrolls three whole rows a notch.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        e.Handled = true;
        ScrollBy(e.Delta.Y > 0 ? -WheelRows : WheelRows);
    }

    /// <summary>Scrolls by whole rows (never past the first or last). Not a selection change.</summary>
    public void ScrollBy(int rows)
    {
        var next = Math.Clamp(_first + rows, 0, Math.Max(0, _rows.Count - Fit));
        if (next == _first) return;
        _first = next;
        InvalidateVisual();
    }
}
