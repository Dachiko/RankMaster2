using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Ranking.Tests;

public class TrueSkillTests
{
    [Fact]
    public void FirstWin_MovesMeansAndShrinksSigma()
    {
        var engine = new TrueSkill();
        var (w, l) = engine.Update(RankingConstants.DefaultRating, RankingConstants.DefaultRating);

        Assert.True(w.Mu > RankingConstants.InitialMu);
        Assert.True(l.Mu < RankingConstants.InitialMu);
        Assert.True(w.Sigma < RankingConstants.InitialSigma);
        Assert.True(l.Sigma < RankingConstants.InitialSigma);
        Assert.InRange(w.Mu + l.Mu, 49.9, 50.1);
    }

    [Fact]
    public void LibraryProgress_IsZeroOnFreshLibrary()
    {
        var rec = new MediaRecord(new MediaId("a.jpg"), MediaKind.Still, RankingConstants.DefaultRating, 0, 0, 0);
        Assert.Equal(0, LibraryProgress.Of([rec]));
    }

    [Fact]
    public void ConservativeScore_IsMuMinusThreeSigma()
    {
        var r = new Rating(26, 2);
        Assert.Equal(20, r.ConservativeScore);
    }

    [Fact]
    public void ApplyVote_BumpsMatchesAndImpressions()
    {
        var a = new MediaRecord(new MediaId("a.jpg"), MediaKind.Still, RankingConstants.DefaultRating, 0, 0, 0);
        var b = new MediaRecord(new MediaId("b.jpg"), MediaKind.Still, RankingConstants.DefaultRating, 0, 0, 0);
        var (w, l) = RecordUpdates.ApplyVote(new TrueSkill(), a, b, 99);
        Assert.Equal(1, w.Matches);
        Assert.Equal(1, l.Matches);
        Assert.Equal(1, w.Impressions);
        Assert.Equal(99, w.LastPlayed);
        Assert.True(w.Rating.Mu > l.Rating.Mu);
    }

    [Fact]
    public void ApplySkip_OnlyIncrementsImpressions()
    {
        var a = new MediaRecord(new MediaId("a.jpg"), MediaKind.Still, RankingConstants.DefaultRating, 2, 4, 10);
        var s = RecordUpdates.ApplySkip(a);
        Assert.Equal(5, s.Impressions);
        Assert.Equal(2, s.Matches);
        Assert.Equal(a.Rating, s.Rating);
        Assert.Equal(10, s.LastPlayed);
    }

    [Fact]
    public void Upset_DoesNotProduceNaN()
    {
        var engine = new TrueSkill();
        var favorite = new Rating(40, 0.5);
        var underdog = new Rating(10, 0.5);
        var (w, l) = engine.Update(underdog, favorite);
        Assert.False(double.IsNaN(w.Mu));
        Assert.False(double.IsInfinity(w.Mu));
        Assert.True(w.Mu > underdog.Mu);
        Assert.True(l.Mu < favorite.Mu);
    }
}
