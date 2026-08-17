using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Ranking.Tests;

public class RankingSessionTests
{
    [Fact]
    public void Drop_RemovesFileAndDoesNotCountTheOtherAsSeen()
    {
        var catalog = new MemoryCatalog(
            Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"), Rec("d.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), prefetchPairs: 1);
        Assert.True(session.Start());
        Assert.NotNull(session.Current);
        var dropped = session.Current!.Value.Left;
        var other = session.Current.Value.Right;
        var beforeImpressions = session.Find(other).Impressions;

        session.Drop(dropped);

        Assert.DoesNotContain(session.Records, r => r.Id == dropped);
        Assert.Equal(beforeImpressions, session.Find(other).Impressions);
        Assert.True(session.Current is null || !session.Current.Value.Contains(dropped));
    }

    [Fact]
    public void Drop_LastTwo_ClearsCurrent()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), prefetchPairs: 1);
        Assert.True(session.Start());
        session.Drop(session.Current!.Value.Left);
        Assert.Null(session.Current);
        Assert.Single(session.Records);
    }

    [Fact]
    public void MixedLibrary_OnlyPairsStills()
    {
        var catalog = new MemoryCatalog(
            Rec("a.jpg"), Rec("b.jpg"), Rec("c.mp4"), Rec("d.mp4"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), 1);
        Assert.True(session.Start());
        Assert.NotNull(session.Current);
        Assert.EndsWith(".jpg", session.Current!.Value.Left.Filename);
        Assert.EndsWith(".jpg", session.Current.Value.Right.Filename);
        Assert.Equal(2, session.Rankable.Count);
    }

    [Fact]
    public void Restore_PutsFileBack()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), 1);
        session.Start();
        var gone = session.Drop(session.Current!.Value.Left);
        Assert.NotNull(gone);
        session.Restore(gone!);
        Assert.Contains(session.Records, r => r.Id == gone!.Id);
    }

    [Fact]
    public void Restore_AfterLastDrop_ResumesPair()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), 1);
        session.Start();
        var gone = session.Drop(session.Current!.Value.Left);
        Assert.Null(session.Current);
        session.Restore(gone!);
        Assert.NotNull(session.Current);
        Assert.Equal(2, session.Records.Count);
    }

    private static MediaRecord Rec(string name) =>
        new(new MediaId(name), MediaExtensions.KindOf(name) ?? MediaKind.Still, RankingConstants.DefaultRating, 0, 0, 0);

    private sealed class MemoryCatalog : ICatalog
    {
        private List<MediaRecord> _records;

        public MemoryCatalog(params MediaRecord[] records) => _records = [.. records];

        public IReadOnlyList<MediaRecord> Scan(string folder) => _records;

        public void Save(string folder, IReadOnlyList<MediaRecord> records) =>
            _records = records.ToList();

        public IReadOnlyList<MediaRecord> RemapIds(
            IReadOnlyList<MediaRecord> records,
            IReadOnlyDictionary<MediaId, MediaId> map) =>
            records.Select(r => map.TryGetValue(r.Id, out var n) ? r with { Id = n } : r).ToList();
    }
}
