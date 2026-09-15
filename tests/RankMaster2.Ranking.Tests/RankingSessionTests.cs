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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Vote_OnSmallLibrary_KeepsACurrentPair(int count)
    {
        var files = Enumerable.Range(0, count).Select(i => Rec($"{(char)('a' + i)}.jpg")).ToArray();
        var session = new RankingSession("mem", new MemoryCatalog(files), new TrueSkill(), new PairSelector(), 2);
        Assert.True(session.Start());
        Assert.NotNull(session.Current);
        session.VoteLeft();
        Assert.NotNull(session.Current);
        Assert.Equal(2, session.Current!.Value.Left == session.Current.Value.Right ? 0 : 2);
        Assert.NotEqual(session.Current.Value.Left, session.Current.Value.Right);
    }

    [Fact]
    public void Skip_OnTwoFiles_KeepsACurrentPair()
    {
        var session = new RankingSession("mem", new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg")), new TrueSkill(), new PairSelector(), 2);
        Assert.True(session.Start());
        session.Skip();
        Assert.NotNull(session.Current);
        Assert.NotEqual(session.Current!.Value.Left, session.Current.Value.Right);
    }

    [Fact]
    public void Vote_MixedTwoStillsAndVideo_KeepsACurrentPair()
    {
        var session = new RankingSession(
            "mem",
            new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"), Rec("c.mp4")),
            new TrueSkill(),
            new PairSelector(),
            2);
        Assert.True(session.Start());
        session.VoteLeft();
        Assert.NotNull(session.Current);
        Assert.EndsWith(".jpg", session.Current!.Value.Left.Filename);
        Assert.EndsWith(".jpg", session.Current.Value.Right.Filename);
    }

    [Fact]
    public void Vote_AppendsConfirmationWhenFavoriteWins()
    {
        var session = Session(
            Rec("low.jpg", mu: 20),
            Rec("high.jpg", mu: 30));
        Assert.True(session.Start());
        // Pair is low vs high in some order; vote for the higher μ.
        var left = session.Find(session.Current!.Value.Left);
        var right = session.Find(session.Current.Value.Right);
        if (left.Rating.Mu >= right.Rating.Mu)
            session.VoteLeft();
        else
            session.VoteRight();
        Assert.Equal(new[] { MatchCue.Confirmation }, session.RecentCues);
    }

    [Fact]
    public void Vote_AppendsUpsetWhenUnderdogWins()
    {
        var session = Session(
            Rec("low.jpg", mu: 20),
            Rec("high.jpg", mu: 30));
        Assert.True(session.Start());
        var left = session.Find(session.Current!.Value.Left);
        var right = session.Find(session.Current.Value.Right);
        if (left.Rating.Mu < right.Rating.Mu)
            session.VoteLeft();
        else
            session.VoteRight();
        Assert.Equal(new[] { MatchCue.Upset }, session.RecentCues);
    }

    [Fact]
    public void Vote_TiedMu_IsConfirmation()
    {
        var session = Session(Rec("a.jpg"), Rec("b.jpg"));
        Assert.True(session.Start());
        session.VoteLeft();
        Assert.Equal(MatchCue.Confirmation, session.RecentCues.Single());
    }

    [Fact]
    public void Skip_DoesNotAddACue()
    {
        var session = Session(Rec("a.jpg"), Rec("b.jpg"));
        Assert.True(session.Start());
        session.Skip();
        Assert.Empty(session.RecentCues);
    }

    [Fact]
    public void Vote_KeepsOnlyLastTenCues()
    {
        var session = Session(Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"));
        Assert.True(session.Start());
        for (var i = 0; i < 11; i++)
            session.VoteLeft();
        Assert.Equal(10, session.RecentCues.Count);
    }

    [Fact]
    public void Start_ClearsCues()
    {
        var session = Session(Rec("a.jpg"), Rec("b.jpg"));
        Assert.True(session.Start());
        session.VoteLeft();
        Assert.NotEmpty(session.RecentCues);
        Assert.True(session.Start());
        Assert.Empty(session.RecentCues);
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

    [Fact]
    public void Restore_WhileAPairIsOnScreen_DoesNotKeepOldPairReserved()
    {
        // Sigmas decide the pick order: (s1, s2) on screen, s3 first in line.
        var catalog = new MemoryCatalog(
            Rec("s1.jpg", sigma: 9),
            Rec("s2.jpg", sigma: 8),
            Rec("s3.jpg", sigma: 7));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), 1);
        Assert.True(session.Start());
        Assert.Equal(new MediaId("s1.jpg"), session.Current!.Value.Left);

        // A file comes back while the old pair is still showing (undo path).
        session.Restore(Rec("s4.jpg", sigma: 10));

        // The restored id must be pairable now, not locked out by stale reservations.
        var pair = session.Current;
        Assert.NotNull(pair);
        Assert.True(
            pair!.Value.Contains(new MediaId("s4.jpg")),
            $"Restored s4.jpg not paired; got {pair.Value.Left} vs {pair.Value.Right}");
    }

    private static RankingSession Session(params MediaRecord[] files) =>
        new("mem", new MemoryCatalog(files), new TrueSkill(), new PairSelector(), 2);

    // ---- one-level action undo (SPEC.md § Ranking) ---------------------------------------------

    [Fact]
    public void UndoLastAction_PutsEveryRatingAndCounterBackExactly()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"), Rec("d.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), prefetchPairs: 1);
        Assert.True(session.Start());

        var pair = session.Current!.Value;
        var before = session.Records.ToDictionary(r => r.Id, r => r);
        Assert.False(session.CanUndoLastAction);

        session.VoteLeft();
        Assert.True(session.CanUndoLastAction);
        Assert.Equal(1, session.SessionVotes);
        Assert.Single(session.RecentCues);

        Assert.True(session.UndoLastAction());

        foreach (var (id, was) in before)
        {
            var now = session.Find(id);
            Assert.Equal(was.Rating.Mu, now.Rating.Mu);
            Assert.Equal(was.Rating.Sigma, now.Rating.Sigma);
            Assert.Equal(was.Matches, now.Matches);
            Assert.Equal(was.Impressions, now.Impressions);
            Assert.Equal(was.LastPlayed, now.LastPlayed);
        }

        Assert.Equal(0, session.SessionVotes);
        Assert.Empty(session.RecentCues);
        Assert.Equal(pair, session.Current);
        Assert.False(session.CanUndoLastAction);
    }

    [Fact]
    public void UndoLastAction_IsOneLevel()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"), Rec("d.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), prefetchPairs: 1);
        Assert.True(session.Start());

        session.VoteLeft();
        session.VoteLeft();

        Assert.True(session.UndoLastAction());
        Assert.False(session.UndoLastAction());
        Assert.Equal(1, session.SessionVotes);
    }

    [Fact]
    public void UndoLastAction_IsGivenUpWhenTheRecordSetChangesUnderneath()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"), Rec("d.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), prefetchPairs: 1);
        Assert.True(session.Start());

        session.VoteLeft();
        Assert.True(session.CanUndoLastAction);

        // A discard moves a file out of the folder. The snapshot still names it, so restoring would
        // resurrect a record whose file has gone - the point is dropped instead.
        session.Drop(session.Current!.Value.Left);

        Assert.False(session.CanUndoLastAction);
        Assert.False(session.UndoLastAction());
    }

    [Fact]
    public void UndoLastAction_TakesBackASkipToo()
    {
        var catalog = new MemoryCatalog(Rec("a.jpg"), Rec("b.jpg"), Rec("c.jpg"), Rec("d.jpg"));
        var session = new RankingSession("mem", catalog, new TrueSkill(), new PairSelector(), prefetchPairs: 1);
        Assert.True(session.Start());

        var pair = session.Current!.Value;
        var impressions = session.Find(pair.Left).Impressions;

        session.Skip();
        Assert.True(session.CanUndoLastAction);
        Assert.True(session.UndoLastAction());

        Assert.Equal(pair, session.Current);
        Assert.Equal(impressions, session.Find(pair.Left).Impressions);
    }

    private static MediaRecord Rec(string name, double mu = RankingConstants.InitialMu, double sigma = RankingConstants.InitialSigma) =>
        new(new MediaId(name), MediaExtensions.KindOf(name) ?? MediaKind.Still, new Rating(mu, sigma), 0, 0, 0);

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
