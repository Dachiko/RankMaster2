namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link.Wire;

/// <summary>Why the browser is open (plan I § 1): <c>O</c> ranks the folder chosen, <c>R</c> asks the
/// rename question for it.</summary>
public enum BrowseMode { Rank, Rename }

public enum BrowseActionKind { None, Rank, Rename, Go }

/// <summary>
/// What a key or a click on the browser asks the coordinator to do next. <see cref="BrowseModel"/> never
/// calls the link; it hands one of these back. <see cref="Go"/> shows another folder (or THIS PC when
/// <see cref="Roots"/>) and selects <see cref="Select"/> in it once it lands; <see cref="HereOpenable"/> is
/// what the model already knows about whether the folder it goes into can be chosen (null = unknown).
/// </summary>
public sealed record BrowseAction(BrowseActionKind Kind, string? Path = null, string? Select = null, bool Roots = false, bool? HereOpenable = null)
{
    public static readonly BrowseAction None = new(BrowseActionKind.None);
}

/// <summary>
/// One row of the browser: a folder name and nothing else (plan I § 2.1). <see cref="Dimmed"/> rows (≈ 35 %)
/// are folders the mode cannot act on, a root that is not available, or a folder that cannot be read; no
/// text says why. <see cref="Far"/> rows are the ones that do not match the typed letters. A match carries the
/// span to underline.
/// </summary>
public sealed record BrowseRow(string Name, string Path, bool Openable, bool Enterable, bool Dimmed)
{
    public bool Far { get; init; }
    public int MatchStart { get; init; } = -1;
    public int MatchLength { get; init; }
}

/// <summary>
/// The folder browser as data (plan I § 2): no Avalonia type, no link call. <c>Views/BrowseView</c> draws
/// this; <see cref="RankCoordinator"/> feeds it the link's answers and carries out the
/// <see cref="BrowseAction"/>s it returns.
/// <para/>
/// <b>Selection</b> is an index into <see cref="Rows"/>, or <see cref="Here"/> (the path's last segment, the
/// folder being shown; plan I § 2.2 item 4), or <see cref="Nothing"/> (letters were typed and no folder
/// matches). <b>Loading</b> never empties the screen: the rows and the path stay until the new listing lands,
/// and an answer for a folder already left is dropped by its sequence number (item 9).
/// </summary>
public sealed class BrowseModel
{
    public const int Here = -1;
    public const int Nothing = -2;

    /// <summary>The title of the one top-level place that lists the drives.</summary>
    public const string ThisPc = "THIS PC";

    private List<BrowseRow> _all = [];
    private int _seq;
    private string _filter = "";
    private string? _pendingSelect;
    private bool? _pendingHereOpenable;
    private string? _target;
    private bool _targetIsRoots;

    public BrowseMode Mode { get; private set; } = BrowseMode.Rank;

    /// <summary>The folder shown, as the server spelled it; null at THIS PC and before the first listing.</summary>
    public string? Path { get; private set; }

    /// <summary>The server's parent of <see cref="Path"/>; null at a drive or share root (and at THIS PC).</summary>
    public string? Parent { get; private set; }

    /// <summary>THIS PC is showing (the drives).</summary>
    public bool AtRoots { get; private set; }

    /// <summary>A listing (or the roots) has landed at least once since <see cref="Reset"/>.</summary>
    public bool Landed { get; private set; }

    /// <summary>Whether the folder being shown can be chosen, as far as the listing it was entered from said;
    /// null when unknown (the browser started there, or went up into it).</summary>
    public bool? HereOpenable { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>A folder is being opened (Rank mode, Enter): the busy line runs and every key waits.</summary>
    public bool Opening { get; private set; }

    /// <summary>The one accent line for a failure (plan I § 2.2 item 10); null when there is none.</summary>
    public string? Status { get; private set; }

    public int SelectedIndex { get; private set; } = Here;

    public string Filter => _filter;

    /// <summary>The rows as shown: matches first (best match on top), then the rest, in natural order.</summary>
    public IReadOnlyList<BrowseRow> Rows { get; private set; } = [];

    /// <summary>Counts every time a listing or the roots land; the view glides rows only when the order changed
    /// within the same landing (typing), never when new rows arrive.</summary>
    public int ListingVersion { get; private set; }

    /// <summary>How many rows a page is (PageUp / PageDown); the view sets it from its height.</summary>
    public int PageSize { get; set; } = 10;

    public int MatchCount => _filter.Length == 0 ? Rows.Count : Rows.Count(r => !r.Far);

    // ---- what the screen says about where it is -----------------------------------------------------------------

    /// <summary>The path line's pieces (not yet shortened for the column): <c>THIS PC</c>, or the folder's path
    /// split by <see cref="BrowsePath.Split"/>. Before the first listing it is the folder being asked for.</summary>
    public IReadOnlyList<PathSegment> Segments
    {
        get
        {
            var thisPc = new[] { new PathSegment(ThisPc, "") };
            if (Landed) return AtRoots ? thisPc : BrowsePath.Split(Path ?? "");
            if (_targetIsRoots) return thisPc;
            return _target is null ? [] : BrowsePath.Split(_target);
        }
    }

    /// <summary>The corner's word after <c>RANK MASTER 3 /</c>.</summary>
    public string ModeWord => Mode == BrowseMode.Rename ? "RENAME" : "OPEN";

    /// <summary>The faint caps key line at the bottom (plan I § 2.2 item 8).</summary>
    public string KeyHint
    {
        get
        {
            var enter = Mode == BrowseMode.Rename ? "RENAME" : "RANK";
            return AtRoots || (!Landed && _targetIsRoots)
                ? "ENTER / → INTO · ESC BACK"
                : $"ENTER {enter} · → INTO · ← UP · ESC BACK";
        }
    }

    /// <summary>The empty-folder line shows only once a listing has landed and it had no folders.</summary>
    public bool ShowsEmptyLine => Landed && Rows.Count == 0;

    // ---- life cycle -----------------------------------------------------------------------------------------------

    /// <summary>The browser is opened afresh in <paramref name="mode"/>: nothing is shown until the first load lands.</summary>
    public void Reset(BrowseMode mode)
    {
        _seq++;
        Mode = mode;
        Path = null;
        Parent = null;
        AtRoots = false;
        Landed = false;
        HereOpenable = null;
        Loading = false;
        Opening = false;
        Status = null;
        _filter = "";
        _all = [];
        Rows = [];
        SelectedIndex = Here;
        _target = null;
        _targetIsRoots = false;
        _pendingSelect = null;
        _pendingHereOpenable = null;
        ListingVersion++;
    }

    /// <summary>The browser is left: whatever is still loading is dropped when it lands.</summary>
    public void Cancel()
    {
        _seq++;
        Loading = false;
        Opening = false;
    }

    /// <summary>A listing (or, with a null <paramref name="path"/>, the roots) is being asked for. Returns the
    /// sequence number the answer must carry back. The screen keeps what it shows; the status line is cleared.</summary>
    public int BeginLoad(string? path, string? selectPath = null, bool? hereOpenable = null)
    {
        _seq++;
        Loading = true;
        Status = null;
        _target = path;
        _targetIsRoots = path is null;
        _pendingSelect = selectPath;
        _pendingHereOpenable = hereOpenable;
        return _seq;
    }

    /// <summary>A listing arrived. False (and nothing changes) when it is for a folder already left.</summary>
    public bool ApplyListing(int seq, FolderListing listing, string? status = null)
    {
        if (seq != _seq) return false;
        Path = listing.Path;
        Parent = listing.Parent;
        AtRoots = false;
        HereOpenable = _pendingHereOpenable;
        _all = listing.Entries
            .Where(e => !IsHiddenName(e.Name))
            .OrderBy(e => e.Name, NaturalOrder.Instance)
            .Select(e =>
            {
                var openable = e.Accessible && IsOpenable(e);
                return new BrowseRow(e.Name, e.Path, openable, e.Accessible, Dimmed: !openable);
            })
            .ToList();
        FinishLanding(status);
        return true;
    }

    /// <summary>The drives arrived (THIS PC). False when dropped as stale.</summary>
    public bool ApplyRoots(int seq, IReadOnlyList<LibraryRoot> roots, string? status = null)
    {
        if (seq != _seq) return false;
        Path = null;
        Parent = null;
        AtRoots = true;
        HereOpenable = false;
        _all = roots
            .Select(r => new BrowseRow(BrowsePath.RootName(r.Path), r.Path, Openable: false, Enterable: r.Available, Dimmed: !r.Available))
            .ToList();
        FinishLanding(status);
        return true;
    }

    /// <summary>A load failed: the screen stays where it was and the accent line says why. False when dropped as stale.</summary>
    public bool Fail(int seq, string text)
    {
        if (seq != _seq) return false;
        Loading = false;
        Status = text;
        return true;
    }

    /// <summary>Rank mode, Enter on a folder: the busy line runs and keys wait. Drops any listing still on its way.</summary>
    public void BeginOpening()
    {
        _seq++;
        Loading = true;
        Opening = true;
        Status = null;
    }

    /// <summary>The open ended without leaving the browser: the folder was refused (or the call failed).</summary>
    public void EndOpening(string? failure)
    {
        Loading = false;
        Opening = false;
        Status = failure;
    }

    private void FinishLanding(string? status)
    {
        Landed = true;
        Loading = false;
        Status = status;
        _filter = "";
        Rows = _all;
        ListingVersion++;
        var wanted = _pendingSelect is null ? -1 : _all.FindIndex(r => BrowsePath.Same(r.Path, _pendingSelect));
        SelectedIndex = wanted >= 0 ? wanted : _all.Count > 0 ? 0 : Here;
        _pendingSelect = null;
        _pendingHereOpenable = null;
    }

    // ---- rules --------------------------------------------------------------------------------------------------------

    /// <summary>Plan I § 2.2 item 3: hidden and system folders are left out by name (a <c>.</c> or <c>$</c> prefix,
    /// <c>System Volume Information</c>; <c>$RECYCLE.BIN</c> is the <c>$</c> rule).</summary>
    public static bool IsHiddenName(string name) =>
        name.Length == 0 || name[0] is '.' or '$' || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plan I § 1 "openable": Rank → <c>rankable</c>; Rename → <c>rankable &amp;&amp; hasDatabase</c>. An unknown
    /// <c>rankable</c> (null) is not openable.</summary>
    private bool IsOpenable(FolderEntry e) =>
        e.Rankable == true && (Mode == BrowseMode.Rank || e.HasDatabase);

    // ---- selection -------------------------------------------------------------------------------------------------------

    /// <summary>Moves by whole rows. <b>Here</b> sits above row 0, so ↑ from the first row lands on it.</summary>
    private void MoveBy(int delta)
    {
        var position = SelectedIndex + 1; // 0 = here, 1.. = rows; Nothing counts as here
        if (position < 0) position = 0;
        position = Math.Clamp(position + delta, 0, Rows.Count);
        SelectedIndex = position - 1;
    }

    public void MoveUp() => MoveBy(-1);
    public void MoveDown() => MoveBy(1);
    public void PageUp() => MoveBy(-Math.Max(1, PageSize));
    public void PageDown() => MoveBy(Math.Max(1, PageSize));
    public void Home() => SelectedIndex = Rows.Count > 0 ? 0 : Here;
    public void End() => SelectedIndex = Rows.Count > 0 ? Rows.Count - 1 : Here;

    /// <summary>A click: -1 selects <b>here</b>; an index outside the rows is ignored.</summary>
    public void Select(int index)
    {
        if (index == Here || (index >= 0 && index < Rows.Count)) SelectedIndex = index;
    }

    // ---- typing to find ----------------------------------------------------------------------------------------------------

    /// <summary>Plan I § 2.2 item 5: typed letters show after the path; matching folders glide to the top, the rest
    /// stay below dimmed; the best match is selected. Control characters and leading spaces are ignored.</summary>
    public void Type(string text)
    {
        var changed = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || (_filter.Length == 0 && c == ' ')) continue;
            _filter += c;
            changed = true;
        }
        if (changed) ApplyFilter();
    }

    /// <summary>Deletes the last typed letter. False (nothing done) when none is typed: the caller then goes up.</summary>
    public bool Backspace()
    {
        if (_filter.Length == 0) return false;
        _filter = _filter[..^1];
        ApplyFilter();
        return true;
    }

    /// <summary>Esc with letters typed: clears them. False when none is typed: the caller then leaves the browser.</summary>
    public bool ClearFilter()
    {
        if (_filter.Length == 0) return false;
        _filter = "";
        ApplyFilter();
        return true;
    }

    private void ApplyFilter()
    {
        var kept = SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex].Path : null;

        if (_filter.Length == 0)
        {
            Rows = _all;
            // Clearing keeps the folder that was selected selected (it was the best match); the rows glide back.
            var again = kept is null ? -1 : _all.FindIndex(r => r.Path == kept);
            SelectedIndex = again >= 0 ? again : _all.Count > 0 ? 0 : Here;
            return;
        }

        var matches = new List<BrowseRow>();
        var rest = new List<BrowseRow>();
        foreach (var row in _all)
        {
            var at = row.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) matches.Add(row with { MatchStart = at, MatchLength = _filter.Length });
            else rest.Add(row with { Far = true });
        }

        // The best match is one that starts with the letters; the others keep natural order after it.
        Rows = matches.OrderBy(r => r.MatchStart == 0 ? 0 : 1).Concat(rest).ToList();
        SelectedIndex = matches.Count > 0 ? 0 : Nothing;
    }

    // ---- what a key means -----------------------------------------------------------------------------------------------------

    /// <summary>Enter: choose the selected folder (Rank → open it, Rename → the rename question), or go into a folder the
    /// mode cannot choose. On <b>here</b> it chooses the folder being shown (unless the listing it was entered from said it
    /// cannot be chosen). Letters typed with no match: nothing.</summary>
    public BrowseAction Activate()
    {
        if (SelectedIndex == Nothing) return BrowseAction.None;
        var kind = Mode == BrowseMode.Rename ? BrowseActionKind.Rename : BrowseActionKind.Rank;

        if (SelectedIndex == Here)
        {
            if (AtRoots || !Landed || Path is null || HereOpenable == false) return BrowseAction.None;
            return new BrowseAction(kind, Path);
        }

        var row = Rows[SelectedIndex];
        if (row.Openable) return new BrowseAction(kind, row.Path);
        return Into(row);
    }

    /// <summary>→ : go into the selected folder; nothing on <b>here</b> or on a folder that cannot be entered.</summary>
    public BrowseAction GoInto() =>
        SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Into(Rows[SelectedIndex]) : BrowseAction.None;

    private BrowseAction Into(BrowseRow row) =>
        row.Enterable ? new BrowseAction(BrowseActionKind.Go, row.Path, HereOpenable: row.Openable) : BrowseAction.None;

    /// <summary>← : up one folder, selecting the one just left; from a drive to THIS PC; nothing at THIS PC.</summary>
    public BrowseAction GoUp()
    {
        if (!Landed || AtRoots || Path is null) return BrowseAction.None;
        return Parent is { } parent
            ? new BrowseAction(BrowseActionKind.Go, parent, Select: Path)
            : new BrowseAction(BrowseActionKind.Go, Roots: true, Select: Path);
    }

    /// <summary>A click on a path segment goes there (and selects the folder it came from). A click on <b>here</b> selects it.</summary>
    public BrowseAction GoToSegment(PathSegment segment)
    {
        var all = Segments;
        var index = all.ToList().FindIndex(s => s.Target == segment.Target);
        if (index < 0) return BrowseAction.None;
        if (index == all.Count - 1)
        {
            Select(Here);
            return BrowseAction.None;
        }
        return new BrowseAction(BrowseActionKind.Go, segment.Target, Select: all[index + 1].Target);
    }
}
