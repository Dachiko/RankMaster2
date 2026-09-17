// AUDIT2.md § 2.1's second half: "a picture that could not be decoded for want of room must not be
// reported to him as a damaged file." The still source already distinguishes the two
// (StillFailure.TooLarge against StillFailure.NotAnImage, and IStillSource's own contract says
// "same offers as NotAnImage, different sentence"); until now the surface threw the distinction
// away and showed one sentence for both.
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

public class PaneFailureSentenceTests
{
    [Fact]
    public async Task A_picture_too_big_for_the_decode_budget_is_not_called_a_file_that_will_not_decode()
    {
        var (c, stills) = await OpenOnAPairOfStills();

        stills.Deliver("a.jpg", new StillState.Failed(StillFailure.TooLarge, "a.jpg (9000 × 9000) is too large to show inside the memory budget."));
        stills.Deliver("b.jpg", new StillState.Failed(StillFailure.NotAnImage, "b.jpg is not a valid image."));

        var tooBig = c.Rank.Left!.Sentence!;
        var broken = c.Rank.Right!.Sentence!;

        Assert.NotEqual(broken, tooBig);
        Assert.Contains("too big", tooBig);
        Assert.Contains("not damaged", tooBig);

        // Same offers as a file that really will not decode -- only the sentence differs.
        Assert.Equal(PaneKind.Undecodable, c.Rank.Left.Kind);
        Assert.True(c.Rank.Left.Accepts(Intent.DiscardLeft));
        Assert.True(c.Rank.Left.Accepts(Intent.VoteRight));
        Assert.False(c.Rank.Left.Accepts(Intent.VoteLeft));
    }

    [Fact]
    public async Task A_file_that_really_will_not_decode_still_says_so()
    {
        var (c, stills) = await OpenOnAPairOfStills();

        stills.Deliver("a.jpg", new StillState.Failed(StillFailure.NotAnImage, "a.jpg is not a valid image."));

        Assert.Equal(PaneKind.Undecodable, c.Rank.Left!.Kind);
        Assert.Contains("cannot be shown", c.Rank.Left.Sentence!);
        Assert.DoesNotContain("too big", c.Rank.Left.Sentence!);
    }

    private static async Task<(RankCoordinator Coordinator, FakeStillSource Stills)> OpenOnAPairOfStills()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking(leftId: "a.jpg", rightId: "b.jpg") };
        var stills = new FakeStillSource();
        var c = new RankCoordinator(link, stills, new FakeVideoSurfaceFactory(), new FakeClock(), new SynchronousUiThread(), new ImmediateDelay(),
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-sentence-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        link.OpenResults.Enqueue(new OpenResult.Opened(link.Snapshot!, false));
        Assert.True(await c.OpenFolderAsync(link.Snapshot!.Folder));
        return (c, stills);
    }
}
