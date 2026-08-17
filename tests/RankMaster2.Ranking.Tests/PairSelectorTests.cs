using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Ranking.Tests;

public class PairSelectorTests
{
    private static MediaRecord Rec(string name, int matches, double sigma = 8.333, double mu = 25) =>
        new(new MediaId(name), MediaKind.Still, new Rating(mu, sigma), matches, 0, 0);

    [Fact]
    public void TwoUnplaced_ArePairedTogether()
    {
        var sel = new PairSelector();
        var pair = sel.SelectNextPair([Rec("a.jpg", 0), Rec("b.jpg", 0)], new HashSet<MediaId>());
        Assert.NotNull(pair);
        var names = new[] { pair!.Value.Left.Filename, pair.Value.Right.Filename };
        Assert.Contains("a.jpg", names);
        Assert.Contains("b.jpg", names);
    }

    [Fact]
    public void Unplaced_MeetsPlacedAnchor()
    {
        var sel = new PairSelector();
        var pair = sel.SelectNextPair(
            [Rec("new.jpg", 0, sigma: 8.3), Rec("old.jpg", 5, sigma: 2.0, mu: 25)],
            new HashSet<MediaId>());
        Assert.NotNull(pair);
        var names = new HashSet<string> { pair!.Value.Left.Filename, pair.Value.Right.Filename };
        Assert.Contains("new.jpg", names);
        Assert.Contains("old.jpg", names);
    }

    [Fact]
    public void HighestSigma_IsPreferredOncePlaced()
    {
        var sel = new PairSelector();
        var pair = sel.SelectNextPair(
            [
                Rec("certain.jpg", 5, sigma: 1.5, mu: 25),
                Rec("unsure.jpg", 5, sigma: 6.0, mu: 25),
                Rec("also.jpg", 5, sigma: 2.0, mu: 25)
            ],
            new HashSet<MediaId>());
        Assert.NotNull(pair);
        Assert.True(
            pair!.Value.Left.Filename == "unsure.jpg" || pair.Value.Right.Filename == "unsure.jpg");
    }

    [Fact]
    public void SameId_NeverReturnedTwice()
    {
        var sel = new PairSelector();
        var pair = sel.SelectNextPair([Rec("only.jpg", 0)], new HashSet<MediaId>());
        Assert.Null(pair);
    }

    [Fact]
    public void Recent_IsIgnoredIfItWouldLeaveFewerThanTwo()
    {
        var sel = new PairSelector();
        var a = Rec("a.jpg", 0);
        var b = Rec("b.jpg", 0);
        var recent = new HashSet<MediaId> { a.Id, b.Id };
        var pair = sel.SelectNextPair([a, b], recent);
        Assert.NotNull(pair);
    }
}
