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
    public int UnrankedCount => Eligible().Count(r => r.Matches == 0);
    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public bool Start()
    {
        _records.Clear();
        _records.AddRange(_catalog.Scan(Folder));
        _warm.Clear();
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
        }
        catch
        {
            RestoreSnapshot(rollback);
            throw;
        }
    }

    public void Save() => _catalog.Save(Folder, _records);

    public void AbandonCurrent() => Advance();

    public MediaRecord? Drop(MediaId id)
    {
        var i = _records.FindIndex(r => r.Id == id);
        if (i < 0)
            return null;

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
        _records.Add(record);
        FillWarm();
    }

    public void ReplaceAll(IReadOnlyList<MediaRecord> records)
    {
        _records.Clear();
        _records.AddRange(records);
        _warm.Clear();
        _recent.Clear();
        _recentOrder.Clear();
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
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (w, l) = RecordUpdates.ApplyVote(_engine, winner, loser, now);
            Replace(w);
            Replace(l);
            Remember(Current.Value);
            SessionVotes++;
            _catalog.Save(Folder, _records);
            Advance();
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
        _recentOrder.ToList());

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
    }

    private readonly record struct SessionSnap(
        List<MediaRecord> Records,
        Pair? Current,
        int SessionVotes,
        List<Pair> Warm,
        HashSet<MediaId> Recent,
        List<MediaId> RecentOrder);

    private void Replace(MediaRecord record)
    {
        var i = _records.FindIndex(r => r.Id == record.Id);
        if (i >= 0)
            _records[i] = record;
    }
}
