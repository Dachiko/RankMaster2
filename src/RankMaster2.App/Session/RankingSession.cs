using System.IO;
using RankMaster2.Ranking;

namespace RankMaster2;

internal sealed class RankingSession
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
    public IEnumerable<Pair> WarmPairs => _warm;
    public int UnrankedCount => _records.Count(r => r.Matches == 0);
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
        Replace(RecordUpdates.ApplySkip(Find(Current.Value.Left)));
        Replace(RecordUpdates.ApplySkip(Find(Current.Value.Right)));
        Remember(Current.Value);
        _catalog.Save(Folder, _records);
        Advance();
    }

    public void Save() => _catalog.Save(Folder, _records);

    /// <summary>Drop the on-screen pair without treating it as seen (corrupt file, etc.).</summary>
    public void AbandonCurrent() => Advance();

    public MediaRecord Find(MediaId id) =>
        _records.First(r => r.Id == id);

    private void ApplyVote(bool leftWins)
    {
        if (Current is null)
            return;

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

        return _selector.SelectNextPair(_records, _recent, reserved);
    }

    private void Remember(Pair pair)
    {
        RememberId(pair.Left);
        RememberId(pair.Right);
    }

    private void RememberId(MediaId id)
    {
        if (_recent.Add(id))
            _recentOrder.Enqueue(id);
        while (_recentOrder.Count > RankingConstants.RecentShownLimit)
        {
            var old = _recentOrder.Dequeue();
            _recent.Remove(old);
        }
    }

    private void Replace(MediaRecord record)
    {
        var i = _records.FindIndex(r => r.Id == record.Id);
        if (i >= 0)
            _records[i] = record;
    }
}
