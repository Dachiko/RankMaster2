using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 6.1 "PaneState": every transition of § 3.2's acceptance table, plus reuse.</summary>
public class PaneStateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static PaneState Waiting(Side side = Side.Left, long gen = 1, string id = "a.jpg", bool video = false) =>
        PaneState.Waiting(side, gen, id, "v1", video, T0);

    [Fact]
    public void Waiting_accepts_nothing()
    {
        var p = Waiting();
        foreach (var intent in AllPairIntents())
            Assert.False(p.Accepts(intent), intent.ToString());
    }

    [Fact]
    public void Ready_accepts_everything()
    {
        var p = Waiting() with { Kind = PaneKind.Ready };
        foreach (var intent in AllPairIntents())
            Assert.True(p.Accepts(intent), intent.ToString());
    }

    [Fact]
    public void Refining_accepts_everything_same_as_ready()
    {
        var p = Waiting() with { Kind = PaneKind.Refining };
        foreach (var intent in AllPairIntents())
            Assert.True(p.Accepts(intent), intent.ToString());
    }

    [Theory]
    [InlineData(Side.Left, Intent.DiscardLeft, true)]
    [InlineData(Side.Left, Intent.VoteLeft, false)]
    [InlineData(Side.Left, Intent.VoteRight, false)] // "not a vote for either side"
    [InlineData(Side.Left, Intent.SpecialLeft, false)]
    [InlineData(Side.Left, Intent.DiscardRight, false)]
    [InlineData(Side.Right, Intent.DiscardRight, true)]
    [InlineData(Side.Right, Intent.VoteLeft, false)]
    public void Gone_accepts_only_its_own_discard(Side side, Intent intent, bool expected)
    {
        var p = Waiting(side) with { Kind = PaneKind.Gone };
        Assert.Equal(expected, p.Accepts(intent));
    }

    [Theory]
    [InlineData(Intent.DiscardLeft, true)]
    [InlineData(Intent.SpecialLeft, true)]
    [InlineData(Intent.VoteRight, true)]  // vote for the OTHER side
    [InlineData(Intent.VoteLeft, false)]  // not a vote for THIS side
    public void Undecodable_left_accepts_per_table(Intent intent, bool expected)
    {
        var p = Waiting(Side.Left) with { Kind = PaneKind.Undecodable };
        Assert.Equal(expected, p.Accepts(intent));
    }

    [Theory]
    [InlineData(Intent.DiscardRight, true)]
    [InlineData(Intent.SpecialRight, true)]
    [InlineData(Intent.VoteLeft, true)]
    [InlineData(Intent.VoteRight, false)]
    public void Undecodable_right_accepts_per_table(Intent intent, bool expected)
    {
        var p = Waiting(Side.Right) with { Kind = PaneKind.Undecodable };
        Assert.Equal(expected, p.Accepts(intent));
    }

    [Theory]
    [InlineData(Intent.DiscardLeft, true)]
    [InlineData(Intent.SpecialLeft, true)]
    [InlineData(Intent.VoteLeft, false)]
    [InlineData(Intent.VoteRight, false)]
    public void NoVideoEngine_left_accepts_per_table(Intent intent, bool expected)
    {
        var p = Waiting(Side.Left) with { Kind = PaneKind.NoVideoEngine };
        Assert.Equal(expected, p.Accepts(intent));
    }

    // ---- sentences -----------------------------------------------------------------------------

    [Fact]
    public void Gone_sentence_names_the_discard_key_for_its_side()
    {
        var left = Waiting(Side.Left, id: "img.jpg").WithGone();
        Assert.Contains("img.jpg", left.Sentence);
        Assert.Contains("`1`", left.Sentence);

        var right = Waiting(Side.Right, id: "img2.jpg").WithGone();
        Assert.Contains("`2`", right.Sentence);
    }

    [Fact]
    public void Undecodable_sentence_names_discard_and_the_other_vote_key()
    {
        var left = Waiting(Side.Left, id: "bad.jpg").WithUndecodable("broken");
        Assert.Contains("bad.jpg", left.Sentence);
        Assert.Contains("`1`", left.Sentence);
        Assert.Contains("→", left.Sentence);

        var right = Waiting(Side.Right, id: "bad2.jpg").WithUndecodable("broken");
        Assert.Contains("`2`", right.Sentence);
        Assert.Contains("←", right.Sentence);
    }

    // ---- reuse (plan § 3.2) --------------------------------------------------------------------

    [Fact]
    public void Ready_pane_with_unchanged_id_and_mediaVersion_is_reusable()
    {
        var ready = Waiting(id: "a.jpg") with { Kind = PaneKind.Ready };
        Assert.True(ready.ReusableFor("a.jpg", "v1"));
    }

    [Fact]
    public void Reuse_fails_on_mediaVersion_change_even_if_id_is_the_same()
    {
        var ready = Waiting(id: "a.jpg") with { Kind = PaneKind.Ready };
        Assert.False(ready.ReusableFor("a.jpg", "v2"));
    }

    [Fact]
    public void Reuse_fails_on_a_different_id()
    {
        var ready = Waiting(id: "a.jpg") with { Kind = PaneKind.Ready };
        Assert.False(ready.ReusableFor("b.jpg", "v1"));
    }

    [Fact]
    public void A_waiting_pane_is_never_reusable()
    {
        var waiting = Waiting(id: "a.jpg");
        Assert.False(waiting.ReusableFor("a.jpg", "v1"));
    }

    [Fact]
    public void WithGeneration_advances_generation_only()
    {
        var ready = Waiting(id: "a.jpg") with { Kind = PaneKind.Ready, Generation = 5 };
        var next = ready.WithGeneration(6);
        Assert.Equal(6, next.Generation);
        Assert.Equal(PaneKind.Ready, next.Kind);
        Assert.Equal("a.jpg", next.Id);
    }

    private static IEnumerable<Intent> AllPairIntents() =>
    [
        Intent.VoteLeft, Intent.VoteRight,
        Intent.DiscardLeft, Intent.DiscardRight, Intent.SpecialLeft, Intent.SpecialRight,
    ];
}
