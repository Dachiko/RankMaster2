using System.Net.Http;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using RankMaster2.Pc.Link.Wire;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>§ 6.3 "Actions" (A1-A17).</summary>
[Collection("RealServer")]
public sealed class ActionTests(RealServer server)
{
    private async Task<(SessionLink Link, TapHandler Tap, ScratchFolder Scratch, Snapshot Snapshot)> OpenedSession(int fileCount = 6)
    {
        var scratch = new ScratchFolder(fileCount);
        var (link, tap) = LinkFactory.Build(server);
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        return (link, tap, scratch, opened.Snapshot);
    }

    [Fact]
    public async Task A1_VoteLeftApplies()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var leftId = snapshot.Pair!.Left.Id;
        var rightId = snapshot.Pair.Right.Id;

        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var applied = Assert.IsType<ActionResult.Applied>(result);
        Assert.Equal(1L, applied.Snapshot.PairSeq);
        Assert.Equal(1, scratch.MatchesOf(leftId));
        Assert.Equal(1, scratch.MatchesOf(rightId));
        Assert.NotNull(applied.Snapshot.LastAction);
        Assert.Equal(ActionTypes.Vote, applied.Snapshot.LastAction!.Type);
        Assert.StartsWith("pc-", applied.Snapshot.LastAction.ClientRequestId);
        Assert.True(applied.Snapshot.LastAction.ClientRequestId!.Length <= 64);
    }

    [Fact]
    public async Task A2_SkipDoesNotChangeMatches()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var leftId = snapshot.Pair!.Left.Id;

        var result = await link.SkipAsync(snapshot.PairSeq);
        var applied = Assert.IsType<ActionResult.Applied>(result);
        Assert.Equal(0, scratch.MatchesOf(leftId));
        Assert.Equal(1, scratch.ImpressionsOf(leftId));
    }

    [Fact]
    public async Task A3_DiscardLeftMovesTheFile()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var leftId = snapshot.Pair!.Left.Id;
        var result = await link.DiscardAsync(Side.Left, snapshot.PairSeq);
        var applied = Assert.IsType<ActionResult.Applied>(result);

        Assert.True(scratch.InDiscarded(leftId));
        Assert.False(scratch.Exists(leftId));
        Assert.Equal(ActionTypes.Discard, applied.Snapshot.LastAction!.Type);
        Assert.Equal(leftId, applied.Snapshot.LastAction.Id);
    }

    [Fact]
    public async Task A4_SpecialRightMovesTheFile()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var rightId = snapshot.Pair!.Right.Id;
        var result = await link.SpecialAsync(Side.Right, snapshot.PairSeq);
        var applied = Assert.IsType<ActionResult.Applied>(result);

        Assert.True(scratch.InSpecial(rightId));
        Assert.Equal(ActionTypes.Special, applied.Snapshot.LastAction!.Type);
    }

    [Fact]
    public async Task A5_UndoAfterVoteRestoresTheConsumedPair()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var originalIds = new[] { snapshot.Pair!.Left.Id, snapshot.Pair.Right.Id }.OrderBy(x => x).ToArray();

        var voted = Assert.IsType<ActionResult.Applied>(await link.VoteAsync(Side.Left, snapshot.PairSeq));

        var undone = Assert.IsType<ActionResult.Applied>(await link.UndoAsync());
        Assert.Equal(ActionTypes.Undo, undone.Snapshot.LastAction!.Type);
        Assert.Equal(ActionTypes.Vote, undone.Snapshot.LastAction.UndoneType);
        Assert.Equal(0, undone.Snapshot.SessionVotes);

        var restoredIds = new[] { undone.Snapshot.Pair!.Left.Id, undone.Snapshot.Pair.Right.Id }.OrderBy(x => x).ToArray();
        Assert.Equal(originalIds, restoredIds);
    }

    [Fact]
    public async Task A6_UndoAfterDiscardBringsTheFileBack()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var leftId = snapshot.Pair!.Left.Id;
        await link.DiscardAsync(Side.Left, snapshot.PairSeq);
        Assert.True(scratch.InDiscarded(leftId));

        var undone = Assert.IsType<ActionResult.Applied>(await link.UndoAsync());
        Assert.NotNull(undone.Snapshot.LastAction!.RestoredId);
        Assert.False(scratch.InDiscarded(leftId));
    }

    [Fact]
    public async Task A7_UndoWhenNoneAvailableIsNotSent()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;
        Assert.False(snapshot.UndoAvailable);

        tap.ClearSent();
        var result = await link.UndoAsync();
        Assert.Equal(NotSentReason.UndoNotAvailable, Assert.IsType<ActionResult.NotSent>(result).Why);
        Assert.Empty(tap.Sent);
    }

    [Fact]
    public async Task A8_StalePairSeqIsNotSent()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.ClearSent();
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq - 1);
        Assert.Equal(NotSentReason.PairMoved, Assert.IsType<ActionResult.NotSent>(result).Why);
        Assert.Empty(tap.Sent);
    }

    [Fact]
    public async Task A9_ExhaustedFolderRefusesFurtherVotes()
    {
        var (link, _, scratch, snapshot) = await OpenedSession(2);
        await using var linkScope = link;
        using var scratchScope = scratch;

        var discarded = Assert.IsType<ActionResult.Applied>(await link.DiscardAsync(Side.Left, snapshot.PairSeq));
        Assert.True(discarded.Snapshot.IsExhausted);

        var result = await link.VoteAsync(Side.Left, discarded.Snapshot.PairSeq);
        Assert.Equal(NotSentReason.Exhausted, Assert.IsType<ActionResult.NotSent>(result).Why);
    }

    [Fact]
    public async Task A10_SaveIsIdempotentUnderALostResponse()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var saved1 = Assert.IsType<ActionResult.Applied>(await link.SaveAsync());
        Assert.Equal(snapshot.PairToken, saved1.Snapshot.PairToken);
        Assert.Equal(snapshot.PairSeq, saved1.Snapshot.PairSeq);
        Assert.NotNull(saved1.Snapshot.LastSavedAt);

        tap.Script(HttpMethod.Post, "/session/save", ScriptedFault.ForwardThenDropResponse);
        var saved2 = Assert.IsType<ActionResult.Applied>(await link.SaveAsync());

        var saveBodies = tap.Sent.Where(s => s.Path == "/session/save").Select(s => s.BodyText).ToList();
        Assert.True(saveBodies.Count >= 2);
        Assert.All(saveBodies, b => Assert.Equal(saveBodies[0], b));
    }

    [Fact]
    public async Task A11_TwoFileFolderHasNoWarmPairsAndStillWorks()
    {
        var (link, _, scratch, snapshot) = await OpenedSession(2);
        await using var linkScope = link;
        using var scratchScope = scratch;

        Assert.Empty(snapshot.WarmPairs);
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        Assert.IsType<ActionResult.Applied>(result);
    }

    [Fact]
    public async Task A12_MissingFileBecomesDropMissingOnDiscard()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var leftId = snapshot.Pair!.Left.Id;
        File.Delete(scratch.FullPathOf(leftId));

        var refreshed = Assert.IsType<ActionResult.Applied>(await link.RefreshAsync());
        var refreshedPairSeq = refreshed.Snapshot.PairSeq;
        var refreshedLeft = refreshed.Snapshot.Pair!.Left.Id == leftId ? refreshed.Snapshot.Pair.Left : refreshed.Snapshot.Pair.Right;
        Assert.True(refreshedLeft.IsMissing);

        var undoAvailableBefore = refreshed.Snapshot.UndoAvailable;
        var side = refreshed.Snapshot.Pair.Left.Id == leftId ? Side.Left : Side.Right;
        var result = Assert.IsType<ActionResult.Applied>(await link.DiscardAsync(side, refreshedPairSeq));

        Assert.Equal(ActionTypes.DropMissing, result.Snapshot.LastAction!.Type);
        Assert.Equal(undoAvailableBefore, result.Snapshot.UndoAvailable);
    }

    [Fact]
    public async Task A13_OverlappingCallsThrow()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.Delay(500));
        tap.ClearSent();

        var voteTask = link.VoteAsync(Side.Left, snapshot.PairSeq);
        await Task.Delay(50);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SkipAsync(snapshot.PairSeq));

        await voteTask;
        Assert.Single(tap.Sent);
    }

    [Fact]
    public async Task A14_CancellationDuringAnActionYieldsUnknown()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.Delay(500));
        tap.ClearSent();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq, cts.Token);

        var unknown = Assert.IsType<ActionResult.Unknown>(result);
        Assert.Equal("client_cancelled", unknown.Failure.Code);
        Assert.Single(tap.Sent);
        Assert.Equal(snapshot.PairToken, link.Snapshot!.PairToken);
    }

    [Fact]
    public async Task A15_SaveFailedIsSurfacedAndRecoverable()
    {
        var (link, _, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
            var refused = Assert.IsType<ActionResult.Refused>(result);
            Assert.Equal(FailureKind.SaveFailed, refused.Failure.Kind);
            Assert.NotNull(refused.Snapshot);
            Assert.Equal(snapshot.PairToken, refused.Snapshot!.PairToken);
        }
        finally
        {
            File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var retried = Assert.IsType<ActionResult.Applied>(await link.VoteAsync(Side.Left, snapshot.PairSeq));
        Assert.Equal(1L, retried.Snapshot.PairSeq);
    }

    [Fact]
    public async Task A16_SessionBusyIsAbsorbedAfterOneWait()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote",
            ScriptedFault.Fabricate(503, """{"error":{"code":"session_busy","message":"busy","requestId":"r1","details":{"retryAfterSeconds":1}}}"""),
            ScriptedFault.Forward);
        tap.ClearSent();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        stopwatch.Stop();

        Assert.IsType<ActionResult.Applied>(result);
        var bodies = tap.Sent.Where(s => s.Path == "/session/vote").Select(s => s.BodyText).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.True(stopwatch.ElapsedMilliseconds >= 900, $"expected ~1s wait, took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task A17_ServerShuttingDownIsReportedWithoutAResend()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote",
            ScriptedFault.Fabricate(503, """{"error":{"code":"server_shutting_down","message":"bye","requestId":"r1","details":{"retryAfterSeconds":1}}}"""));
        tap.ClearSent();

        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var refused = Assert.IsType<ActionResult.Refused>(result);
        Assert.Equal(FailureKind.ServerShuttingDown, refused.Failure.Kind);

        var voteBodies = tap.Sent.Where(s => s.Path == "/session/vote").ToList();
        Assert.Single(voteBodies);
    }
}
