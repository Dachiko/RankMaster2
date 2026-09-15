namespace RankMaster2.Ranking;

public sealed class RankingSession
{
    private readonly ICatalog _catalog;
    private readonly IRatingEngine _engine;
    private readonly IPairSelector _selector;
    private readonly int _prefetchPairs;
    private readonly List<MediaRecord> _records = [];
    private readonly Queue<MediaId> _recentOrder = new();
    private readonly HashSet<MediaId> _recent = [];
    private readonly Queue<Pair> _warm = new();
    private readonly List<MatchCue> _cues = [];

    /// <summary>
    /// The state as it was immediately before the last successful vote or skip, kept so that one
    /// action can be taken back (SPEC.md § Ranking, SERVER_SPEC.md § 10.10). It is the same
    /// snapshot a failed save rolls back to; the only change is that success no longer throws it
    /// away. Null means there is nothing to take back.
    /// </summary>
    private SessionSnap? _undo;

    public RankingSession(
        string folder,
        ICatalog catalog,
        IRatingEngine engine,
        IPairSelector selector,
        int prefetchPairs)
    {
        Folder = folder;
        _catalog = catalog;
        _engine = engine;
        _selector = selector;
        _prefetchPairs = prefetchPairs;
    }

    public string Folder { get; }
    public Pair? Current { get; private set; }
    public int SessionVotes { get; private set; }
    public IReadOnlyList<MediaRecord> Records => _records;
    public IReadOnlyList<MediaRecord> Rankable => Eligible();
    public IEnumerable<Pair> WarmPairs => _warm;
    public IReadOnlyList<MatchCue> RecentCues => _cues;
    public int UnrankedCount => Eligible().Count(r => r.Matches == 0);
    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public bool Start()
    {
        _records.Clear();
        _records.AddRange(_catalog.Scan(Folder));
        _warm.Clear();
        _cues.Clear();
        _recent.Clear();
        _recentOrder.Clear();
        SessionVotes = 0;
        _undo = null;
        Current = null;
        Current = Pick();
        FillWarm();
        return Current is not null;
    }

    public void VoteLeft() => ApplyVote(leftWins: true);
    public void VoteRight() => ApplyVote(leftWins: false);

    public void Skip()
    {
        if (Current is null)
            return;
        var rollback = Snapshot();
        try
        {
            Replace(RecordUpdates.ApplySkip(Find(Current.Value.Left)));
            Replace(RecordUpdates.ApplySkip(Find(Current.Value.Right)));
            Remember(Current.Value);
            _catalog.Save(Folder, _records);
            Advance();
            _undo = rollback;
        }
        catch
        {
            RestoreSnapshot(rollback);
            throw;
        }
    }

    public void Save() => _catalog.Save(Folder, _records);

    /// <summary>True when <see cref="UndoLastAction"/> has something to take back.</summary>
    public bool CanUndoLastAction => _undo is not null;

    /// <summary>
    /// Takes back the last vote or skip: ratings, match and impression counts, the session vote
    /// count, the cue strip, the recent-shown set and the pair that action consumed all return to
    /// exactly what they were, and the result is saved.
    ///
    /// <para>By snapshot, never by inverse arithmetic — a TrueSkill update does not invert cleanly,
    /// and a rating that drifts a little on every undo is worse than no undo at all.</para>
    ///
    /// <para>One level: a second call finds nothing and returns false. All-or-nothing: if the save
    /// throws, the action stays applied and nothing moves, which is the same guarantee the vote
    /// itself gives.</para>
    /// </summary>
    public bool UndoLastAction()
    {
        if (_undo is not { } point)
            return false;

        var applied = Snapshot();
        try
        {
            RestoreSnapshot(point);
            _catalog.Save(Folder, _records);
            _undo = null;
            return true;
        }
        catch
        {
            RestoreSnapshot(applied);
            throw;
        }
    }

    public MediaRecord? Drop(MediaId id)
    {
        var i = _records.FindIndex(r => r.Id == id);
        if (i < 0)
            return null;

        // A discard or a drop supersedes the vote before it: the snapshot holds a record that is
        // no longer in the folder, and restoring it would resurrect a file that has been moved away.
        _undo = null;

        var removed = _records[i];
        _records.RemoveAt(i);
        _recent.Remove(id);

        var warmKept = new Queue<Pair>();
        foreach (var pair in _warm)
        {
            if (!pair.Contains(id))
                warmKept.Enqueue(pair);
        }
        _warm.Clear();
        foreach (var pair in warmKept)
            _warm.Enqueue(pair);

        if (Current is { } cur && cur.Contains(id))
            Current = null;

        if (_records.Count < 2)
        {
            Current = null;
            _warm.Clear();
            return removed;
        }

        if (Current is null)
            Advance();
        else
            FillWarm();

        return removed;
    }

    public void Restore(MediaRecord record)
    {
        if (_records.Any(r => r.Id == record.Id))
            return;
        _undo = null;
        _records.Add(record);
        // Same rule as after a vote/skip: clear Current first so a 2-3 file
        // library can pair again instead of staying reserved.
        Current = null;
        Current = Pick();
        FillWarm();
    }

    public void ReplaceAll(IReadOnlyList<MediaRecord> records)
    {
        _undo = null;
        _records.Clear();
        _records.AddRange(records);
        _warm.Clear();
        _recent.Clear();
        _recentOrder.Clear();
        _cues.Clear();
        Current = Pick();
        FillWarm();
    }

    public MediaRecord Find(MediaId id) =>
        _records.First(r => r.Id == id);

    public bool TryFind(MediaId id, out MediaRecord record)
    {
        var found = _records.FirstOrDefault(r => r.Id == id);
        if (found is null)
        {
            record = default!;
            return false;
        }
        record = found;
        return true;
    }

    private void ApplyVote(bool leftWins)
    {
        if (Current is null)
            return;

        var rollback = Snapshot();
        try
        {
            var left = Find(Current.Value.Left);
            var right = Find(Current.Value.Right);
            var winner = leftWins ? left : right;
            var loser = leftWins ? right : left;
            RememberCue(winner.Rating.Mu >= loser.Rating.Mu ? MatchCue.Confirmation : MatchCue.Upset);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (w, l) = RecordUpdates.ApplyVote(_engine, winner, loser, now);
            Replace(w);
            Replace(l);
            Remember(Current.Value);
            SessionVotes++;
            _catalog.Save(Folder, _records);
            Advance();
            _undo = rollback;
        }
        catch
        {
            RestoreSnapshot(rollback);
            throw;
        }
    }

    private IReadOnlyList<MediaRecord> Eligible()
    {
        var policy = MediaExtensions.RankPolicy(_records.Select(r => r.Kind));
        return _records.Where(r => r.Kind == policy).ToList();
    }

    private void Advance()
    {
        Current = null;
        Current = _warm.Count > 0 ? _warm.Dequeue() : Pick();
        FillWarm();
    }

    private void FillWarm()
    {
        while (_warm.Count < _prefetchPairs)
        {
            var next = Pick();
            if (next is null)
                break;
            _warm.Enqueue(next.Value);
        }
    }

    private Pair? Pick()
    {
        var reserved = new HashSet<MediaId>();
        if (Current is { } cur)
        {
            reserved.Add(cur.Left);
            reserved.Add(cur.Right);
        }
        foreach (var p in _warm)
        {
            reserved.Add(p.Left);
            reserved.Add(p.Right);
        }

        return _selector.SelectNextPair(Eligible(), _recent, reserved);
    }

    private void RememberCue(MatchCue cue)
    {
        _cues.Add(cue);
        if (_cues.Count > RankingConstants.MatchCueLimit)
            _cues.RemoveAt(0);
    }

    private void Remember(Pair pair)
    {
        RememberId(pair.Left);
        RememberId(pair.Right);
    }

    private void RememberId(MediaId id)
    {
        if (_recent.Contains(id))
        {
            var kept = _recentOrder.Where(x => x != id).ToList();
            _recentOrder.Clear();
            foreach (var item in kept)
                _recentOrder.Enqueue(item);
        }

        _recent.Add(id);
        _recentOrder.Enqueue(id);
        while (_recentOrder.Count > RankingConstants.RecentShownLimit)
        {
            var old = _recentOrder.Dequeue();
            if (!_recentOrder.Contains(old))
                _recent.Remove(old);
        }
    }

    private SessionSnap Snapshot() => new(
        _records.ToList(),
        Current,
        SessionVotes,
        _warm.ToList(),
        _recent.ToHashSet(),
        _recentOrder.ToList(),
        _cues.ToList());

    private void RestoreSnapshot(SessionSnap snap)
    {
        _records.Clear();
        _records.AddRange(snap.Records);
        Current = snap.Current;
        SessionVotes = snap.SessionVotes;
        _warm.Clear();
        foreach (var p in snap.Warm)
            _warm.Enqueue(p);
        _recent.Clear();
        foreach (var id in snap.Recent)
            _recent.Add(id);
        _recentOrder.Clear();
        foreach (var id in snap.RecentOrder)
            _recentOrder.Enqueue(id);
        _cues.Clear();
        _cues.AddRange(snap.Cues);
    }

    private readonly record struct SessionSnap(
        List<MediaRecord> Records,
        Pair? Current,
        int SessionVotes,
        List<Pair> Warm,
        HashSet<MediaId> Recent,
        List<MediaId> RecentOrder,
        List<MatchCue> Cues);

    private void Replace(MediaRecord record)
    {
        var i = _records.FindIndex(r => r.Id == record.Id);
        if (i >= 0)
            _records[i] = record;
    }
}
