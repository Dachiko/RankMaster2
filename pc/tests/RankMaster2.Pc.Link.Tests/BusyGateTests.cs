using System.Net.Http;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>
/// The link half of H9. <c>MainWindow.OnOpened</c> fires <c>ConnectAsync</c> in the background at
/// first frame, with the start screen's Open/Resume buttons already enabled — the first launch on a
/// machine where the tray is not already running. Pressing either while that connect is still
/// running used to throw <see cref="InvalidOperationException"/> out of the busy gate, with no catch
/// anywhere on the caller's side, wedging both buttons disabled with no message. <see
/// cref="SessionLink"/>'s gate now waits for an in-flight <c>ConnectAsync</c> instead, bounded by
/// its own timeouts, and lets the waiting call through once it finishes — the case any *other*
/// concurrent call still throws immediately is covered elsewhere (e.g. two overlapping
/// <c>OpenAsync</c> calls in <c>NegativeTests</c>/<c>ActionTests</c>); that rule did not change.
/// </summary>
[Collection("RealServer")]
public sealed class BusyGateTests(RealServer server)
{
    [Fact]
    public async Task OpenAsync_during_ConnectAsync_waits_for_it_and_then_opens()
    {
        using var scratch = new ScratchFolder(4);
        var credentialDir = Path.Combine(Path.GetTempPath(), "rm2-link-cred", Guid.NewGuid().ToString("N"));
        var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        // A cold connect (no cached credential) goes through the full enrolment round trip. Slow the
        // pairing POST down so it is still in flight — still holding the busy gate — when OpenAsync
        // lands, the same shape as H9's real trigger.
        tap.Script(HttpMethod.Post, "/pair", ScriptedFault.Delay(1800));

        var connect = link.ConnectAsync();
        await Task.Delay(300);
        Assert.True(link.IsBusy);

        var openTask = link.OpenAsync(scratch.Path); // used to throw InvalidOperationException at once

        var connectResult = await connect;
        Assert.IsType<ConnectResult.Connected>(connectResult);

        var openResult = await openTask;
        var opened = Assert.IsType<OpenResult.Opened>(openResult);
        Assert.Equal(scratch.Path, opened.Snapshot.Folder);
        Assert.Equal(LinkState.InSession, link.State);
    }
}
