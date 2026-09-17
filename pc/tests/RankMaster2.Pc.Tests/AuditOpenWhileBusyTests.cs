// AUDIT throwaway test (not production). RankCoordinator.OpenFolderAsync has no catch: when the link
// throws (the real SessionLink throws InvalidOperationException from its busy gate whenever a call
// overlaps another — e.g. MainWindow's startup ConnectAsync still running when the owner presses
// Open/Resume), the start screen is left in Opening=true with both buttons disabled, forever.
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using Xunit;

namespace RankMaster2.Pc.Tests.Audit;

public class AuditOpenWhileBusyTests
{
    [Fact]
    public async Task A_throwing_OpenAsync_leaves_the_start_screen_stuck_in_Opening()
    {
        // FakeSessionLink.OpenAsync throws InvalidOperationException when it has no scripted result
        // and no Snapshot — the same exception type the real link's busy gate throws.
        var link = new FakeSessionLink { Snapshot = null, State = LinkState.Disconnected };
        var c = new RankCoordinator(link, new FakeStillSource(), new FakeVideoSurfaceFactory(),
            new FakeClock(), new SynchronousUiThread(), new ImmediateDelay(),
            new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-audit-" + Guid.NewGuid().ToString("N"), "last-folder.txt")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.OpenFolderAsync("/some/folder"));

        Assert.True(c.Start.Opening, "expected the wedge: Opening stays true after the throw");
        Assert.Equal("/some/folder", c.Start.OpeningFolder);
        Assert.Null(c.Start.BoxText); // and nothing is said to the owner
        Assert.Equal(AppScreen.Start, c.Screen);
    }
}
