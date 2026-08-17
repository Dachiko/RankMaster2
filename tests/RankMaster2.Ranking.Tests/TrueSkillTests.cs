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
    public void ConservativeScore_IsMuMinusThreeSigma()
    {
        var r = new Rating(26, 2);
        Assert.Equal(20, r.ConservativeScore);
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
