using System.Net.Http;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Enrolment;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>
/// Acceptance gate item 7: "A Failure exists for every situation in the § 5.4 'Reported' table ...
/// each has a test that produces it against the real server." W1-N14 exercise most of the table
/// (ServerNotRunning/C5, NotYourServer/C6+N11, PairingFailed/C8+N12, the five folder kinds/L3,
/// FolderLocked/L4, SaveFailed/A15, ServerShuttingDown/A17, Unreachable/N3). This file covers the
/// four rows nothing else reaches: PairingLost, MoveFailed, a *reported* ServerBusy (A16 only
/// exercises the absorbed single-wait case), and Unexpected.
/// </summary>
[Collection("RealServer")]
public sealed class SupplementalFailureTests(RealServer server)
{
    [Fact]
    public async Task MoveFailedIsReportedWhenTheFolderCannotBeWrittenTo()
    {
        using var scratch = new ScratchFolder(6);
        var (link, tap) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var snapshot = opened.Snapshot;
        _ = tap;

        // discarded/ does not exist yet, so FileOps.MoveToSubfolder must create it: with no write
        // permission on the folder itself, that create throws and the move never happens.
        File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await link.DiscardAsync(Side.Left, snapshot.PairSeq);
            var refused = Assert.IsType<ActionResult.Refused>(result);
            Assert.Equal(FailureKind.MoveFailed, refused.Failure.Kind);
        }
        finally
        {
            File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // nothing changed: the file is still exactly where it was, and the same token still works.
        Assert.True(scratch.Exists(snapshot.Pair!.Left.Id));
        var retried = Assert.IsType<ActionResult.Applied>(await link.DiscardAsync(Side.Left, snapshot.PairSeq));
        Assert.True(scratch.InDiscarded(snapshot.Pair.Left.Id));
        _ = retried;
    }

    [Fact]
    public async Task ServerBusyIsReportedAfterASecond503()
    {
        using var scratch = new ScratchFolder(6);
        var (link, tap) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var snapshot = opened.Snapshot;

        var busy = ScriptedFault.Fabricate(503, """{"error":{"code":"session_busy","message":"busy","requestId":"r","details":{"retryAfterSeconds":0}}}""");
        tap.Script(HttpMethod.Post, "/session/vote", busy, busy);
        tap.ClearSent();

        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var refused = Assert.IsType<ActionResult.Refused>(result);
        Assert.Equal(FailureKind.ServerBusy, refused.Failure.Kind);

        var votes = tap.Sent.Where(s => s.Path == "/session/vote").ToList();
        Assert.Equal(2, votes.Count); // the one automatic resend the 503 path allows, never a third
    }

    [Fact]
    public async Task UnexpectedCoversAnUnrecognisedCode()
    {
        using var scratch = new ScratchFolder(6);
        var (link, tap) = LinkFactory.Build(server);
        await using var linkScope = link;
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var snapshot = opened.Snapshot;

        tap.Script(HttpMethod.Post, "/session/vote",
            ScriptedFault.Fabricate(400, """{"error":{"code":"a_code_this_client_has_never_heard_of","message":"?","requestId":"r"}}"""));

        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var refused = Assert.IsType<ActionResult.Refused>(result);
        Assert.Equal(FailureKind.Unexpected, refused.Failure.Kind);
        Assert.Contains("a_code_this_client_has_never_heard_of", refused.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingLostWhenReEnrolmentItselfFailsMidSession()
    {
        var credentialDir = Path.Combine(Path.GetTempPath(), "rm2-link-cred", Guid.NewGuid().ToString("N"));
        using var scratch = new ScratchFolder(6);
        var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var snapshot = opened.Snapshot;

        var credential = Credential.Load(credentialDir)!;
        var revoker = await server.PairSecondDeviceAsync("pairing-lost-revoker");
        using var revokerHttp = revoker.CreateClient();
        var revoke = await revokerHttp.DeleteAsync(server.BaseUrl + "/pair/" + Uri.EscapeDataString(credential.DeviceId));
        revoke.EnsureSuccessStatusCode();

        // The 401 triggers re-enrolment; make that attempt itself fail (a lost /pair response is
        // never retried, N12), so the credential cannot be replaced either.
        tap.Script(HttpMethod.Post, "/pair", ScriptedFault.ForwardThenDropResponse);

        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var refused = Assert.IsType<ActionResult.Refused>(result);
        Assert.Equal(FailureKind.PairingLost, refused.Failure.Kind);
        Assert.True(refused.Failure.Fatal);
    }
}
