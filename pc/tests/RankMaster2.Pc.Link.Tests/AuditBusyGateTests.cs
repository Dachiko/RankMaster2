// AUDIT throwaway test (not production). The real SessionLink's busy gate: an OpenAsync issued while
// ConnectAsync is still in flight (no credential, no server, waiting for a pairing offer) is not
// queued and not refused with a result — it throws InvalidOperationException. MainWindow.OnOpened
// fires ConnectAsync in the background at first frame while the start screen's Open/Resume buttons
// are enabled, so this is reachable by the owner.
using RankMaster2.Pc.Link;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

public sealed class AuditBusyGateTests
{
    [Fact]
    public async Task OpenAsync_while_ConnectAsync_is_in_flight_throws_instead_of_waiting()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "rm2-audit-busy", Guid.NewGuid().ToString("N"));
        var options = new LinkOptions
        {
            CredentialDirectory = Path.Combine(scratch, "cred"),      // no link.json -> enrol path
            ServerDataDirectory = Path.Combine(scratch, "data"),      // no server -> waits for an offer
            StartServerIfNotRunning = false,
            OfferTimeout = TimeSpan.FromSeconds(3),
        };
        await using var link = new SessionLink(options, wrap: null);

        var connect = link.ConnectAsync();
        await Task.Delay(200);                       // the connect is now polling for pairing.json
        Assert.True(link.IsBusy);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.OpenAsync("/nowhere"));
        Assert.Contains("already in flight", ex.Message);

        var result = await connect;                  // the connect finishes on its own after the timeout
        Assert.IsType<ConnectResult.Failed>(result);
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    }
}
