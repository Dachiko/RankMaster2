using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using RankMaster2.Pc.Ui.Surface;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// The in-app folder browser, in house style (plan I § 2.2; mockup F, "5 · Folders"). The <b>path</b> in Doto, upper
/// case, ~44 px: earlier segments and the <c>\</c> separators faint, <b>here</b> (the folder being shown) in ink with a
/// 36×3 red bar under it, turning ink-filled when it is the selection; the typed letters in red after it, with a
/// caret. Under it the <b>rows</b> (<see cref="BrowseList"/>): folder names only. A busy line under the path while a
/// listing loads (it always takes its 2 px, so rows never move when it starts or stops); one accent status line for a
/// failure and one faint caps key line at the bottom; <c>NO FOLDERS INSIDE</c> for an empty folder. Corner:
/// <c>RANK MASTER 3 / OPEN</c> (or <c>/ RENAME</c> in the accent) and <c>F1 KEYS</c>.
/// <para/>
/// Decides nothing: it draws <see cref="RankCoordinator.Browse"/> on every <see cref="Refresh"/> and passes clicks on to the
/// coordinator. Keys and typed text are wired at <see cref="UiRoot"/>'s tunnel handler, as on every other screen. A path
/// too long for the column drops its earliest segments behind <c>…\</c>; <b>here</b> always shows (item 2). Motion is
/// quiet: the new <b>here</b> and a status line decode in when the owner caused them; rows glide when letters reorder them
/// and otherwise land still.
/// </summary>
public partial class BrowseView : UserControl, IRefreshable
{
    private const double PathFontSize = 44;

    private readonly RankCoordinator _coordinator;
    private readonly HouseMotion _motion;
    private readonly TextBlock _modeText;
    private readonly TextBlock _keysCaption;
    private readonly Grid _column;
    private readonly StackPanel _pathPanel;
    private readonly StackPanel _typedPanel;
    private readonly TextBlock _typedText;
    private readonly BusyLine _busy;
    private readonly Border _busyHost;
    private readonly BrowseList _list;
    private readonly TextBlock _emptyLine;
    private readonly TextBlock _statusLine;
    private readonly TextBlock _keyLine;
    private readonly KeysPage _keys;

    private string _pathSignature = "";
    private string _segmentTargets = "";
    private int _lastVersion = -1;

    public BrowseView(RankCoordinator coordinator, HouseMotion motion)
    {
        AvaloniaXamlLoader.Load(this);
        _coordinator = coordinator;
        _motion = motion;

        _modeText = this.FindControl<TextBlock>("ModeText")!;
        _keysCaption = this.FindControl<TextBlock>("KeysCaption")!;
        _column = this.FindControl<Grid>("Column")!;
        _pathPanel = this.FindControl<StackPanel>("PathPanel")!;
        _typedPanel = this.FindControl<StackPanel>("TypedPanel")!;
        _typedText = this.FindControl<TextBlock>("TypedText")!;
        _busy = this.FindControl<BusyLine>("Busy")!;
        _busyHost = (Border)_busy.Parent!;
        _list = this.FindControl<BrowseList>("RowList")!;
        _emptyLine = this.FindControl<TextBlock>("EmptyLine")!;
        _statusLine = this.FindControl<TextBlock>("StatusLine")!;
        _keyLine = this.FindControl<TextBlock>("KeyLine")!;
        _keys = this.FindControl<KeysPage>("Keys")!;
        _keys.SetSections(HelpRows.BrowsePage);

        _list.RowClicked += index => _coordinator.BrowseSelect(index);
        _list.RowActivated += index => _coordinator.BrowseActivate(index);
        _list.SizeChanged += (_, _) => _coordinator.Browse.PageSize = _list.Fit;

        // F1 by mouse: the press the key makes.
        _keysCaption.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _coordinator.BrowseToggleHelp();
        };
        _keys.CloseRequested += () => _coordinator.BrowseToggleHelp();

        // The path may need shortening whenever the column's width changes.
        _column.SizeChanged += (_, _) => Refresh();
    }

    /// <summary>The rows control, for tests.</summary>
    public BrowseList List => _list;

    /// <summary>The path line as it reads on screen (what is shown, after shortening), for tests.</summary>
    public string PathLineText => string.Concat(_pathPanel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    /// <summary>The letters typed so far as shown, for tests.</summary>
    public string TypedLineText => _typedPanel.IsVisible ? _typedText.Text ?? "" : "";

    /// <summary>The keys page is open (what was last asked for), for tests.</summary>
    public bool KeysPageOpen => _keys.IsOpen;

    public void Refresh()
    {
        var browse = _coordinator.Browse;
        var userCaused = _motion.Activity.Recent;

        // ---- corner ----
        _modeText.Text = browse.ModeWord;
        _modeText.Classes.Set("accent", browse.Mode == BrowseMode.Rename);

        // ---- path and typed letters ----
        _typedPanel.IsVisible = browse.Filter.Length > 0;
        _typedText.Text = browse.Filter;
        UpdatePath(browse, userCaused);

        // ---- busy line: under the path, as wide as it ----
        _busy.IsVisible = browse.Loading;
        _busyHost.Width = Math.Max(96, _pathPanel.Bounds.Width);

        // ---- rows ----
        _coordinator.Browse.PageSize = _list.Fit;
        var newListing = browse.ListingVersion != _lastVersion;
        _lastVersion = browse.ListingVersion;
        _list.Sync(browse.Rows, browse.SelectedIndex, newListing);
        _emptyLine.IsVisible = browse.ShowsEmptyLine;

        // ---- bottom ----
        _motion.Change(_statusLine, browse.Status ?? "", userCaused);
        _keyLine.Text = browse.KeyHint;

        _keys.SetOpen(_coordinator.Rank.HelpPinned);
    }

    // ---- the path line --------------------------------------------------------------------------------------------------

    private void UpdatePath(BrowseModel browse, bool userCaused)
    {
        var segments = browse.Segments;
        var hereSelected = browse.SelectedIndex == BrowseModel.Here;
        var targets = string.Join("|", segments.Select(s => s.Target));

        // What the typed letters take from the column: the 26 px gap, the letters (~11 px each) and the caret.
        var reserved = browse.Filter.Length == 0 ? 0 : Math.Min(_column.Bounds.Width * 0.4, 40 + browse.Filter.Length * 11);
        var available = _column.Bounds.Width > 0 ? _column.Bounds.Width - reserved : double.PositiveInfinity;

        var signature = $"{targets}#{hereSelected}#{(int)available / 8}";
        if (signature == _pathSignature) return;
        _pathSignature = signature;

        var separator = segments.Count > 0 ? BrowsePath.SeparatorOf(segments[^1].Target.Length > 0 ? segments[^1].Target : segments[0].Text) : '\\';
        var elided = BrowsePath.Elide(segments, candidate =>
        {
            if (double.IsInfinity(available)) return true;
            var probe = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var piece in BuildPath(candidate, separator, hereSelected, probe: true)) probe.Children.Add(piece);
            probe.Measure(Size.Infinity);
            return probe.DesiredSize.Width <= available;
        });

        _pathPanel.Children.Clear();
        TextBlock? here = null;
        foreach (var piece in BuildPath(elided, separator, hereSelected, probe: false))
        {
            _pathPanel.Children.Add(piece);
            if (piece.Tag is "here") here = piece.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault();
        }

        // The folder just arrived at decodes in when the owner caused it (plan H § 5 S3); a rebuild for another
        // reason (the selection moved onto or off it, the column resized) leaves it still.
        var arrived = targets != _segmentTargets;
        _segmentTargets = targets;
        if (arrived && userCaused && here is not null)
        {
            var text = here.Text ?? "";
            here.Text = "";
            _motion.Change(here, text, userCaused: true);
        }
    }

    private IEnumerable<Control> BuildPath(ElidedPath path, char separator, bool hereSelected, bool probe)
    {
        if (path.Elided) yield return PathText("…" + separator, faint: true);

        for (var i = 0; i < path.Shown.Count; i++)
        {
            var segment = path.Shown[i];
            var text = segment.Text.ToUpperInvariant();

            if (i < path.Shown.Count - 1)
            {
                var piece = PathText(text, faint: true);
                if (!probe) piece.PointerPressed += (_, e) => { e.Handled = true; _coordinator.BrowseGoToSegment(segment); };
                yield return piece;
                if (!text.EndsWith(separator)) yield return PathText(separator.ToString(), faint: true, margin: new Thickness(2, 0));
                continue;
            }

            // here: ink, 8 px of padding that begins 8 px left of the column, the 36x3 red bar 10 px under it.
            var label = PathText(text, faint: false);
            label.Classes.Set("onink", hereSelected);
            var box = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6),
                Child = label,
            };
            if (hereSelected) box.Bind(Border.BackgroundProperty, box.GetResourceObservable("House.Ink"));
            var bar = new Rectangle
            {
                Width = 36,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, -10),
                IsHitTestVisible = false,
            };
            bar.Bind(Shape.FillProperty, bar.GetResourceObservable("House.Accent"));
            var holder = new Grid { Margin = new Thickness(-8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "here", Children = { box, bar } };
            if (!probe) holder.PointerPressed += (_, e) => { e.Handled = true; _coordinator.BrowseGoToSegment(segment); };
            yield return holder;
        }
    }

    private static TextBlock PathText(string text, bool faint, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = PathFontSize, VerticalAlignment = VerticalAlignment.Center };
        block.Classes.Add("dot");
        if (faint) block.Classes.Add("faint");
        if (margin is { } m) block.Margin = m;
        return block;
    }
}
