namespace RankMaster2;

public interface IRatingEngine
{
    (Rating Winner, Rating Loser) Update(Rating winner, Rating loser);
}

public interface IPairSelector
{
    Pair? SelectNextPair(IReadOnlyList<MediaRecord> records, IReadOnlySet<MediaId> recentShownIds);
}

public interface ICatalog
{
    IReadOnlyList<MediaRecord> Scan(string folder);
    void Save(string folder, IReadOnlyList<MediaRecord> records);
    IReadOnlyList<MediaRecord> RemapIds(IReadOnlyList<MediaRecord> records, IReadOnlyDictionary<MediaId, MediaId> map);
}

public interface IMediaPipeline
{
    /// <summary>Pairs held ahead of the pair on screen. Change this one value to change queue depth.</summary>
    int PrefetchPairs { get; }

    void Show(MediaId left, MediaId right);
    void Enqueue(Pair pair);
    void Release(MediaId id);
    void CancelWarmContaining(MediaId id);
}

public interface ILibraryActions
{
    void Discard(MediaId id);
    void MoveToSpecial(MediaId id);
    bool UndoLastMove();
    void RenameByRank();
}
