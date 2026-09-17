using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Enrolment;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using RankMaster2.Pc.Link.Wire;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>
/// § 6.4 "What must never happen" (N1-N14). This is the point of the whole part: every test here
/// asserts that a lost response, a killed server, a revoked token or a concurrent phone cannot turn
/// one intention into two applied actions.
/// </summary>
[Collection("RealServer")]
public sealed class NegativeTests(RealServer server)
{
    private async Task<(SessionLink Link, TapHandler Tap, ScratchFolder Scratch, Snapshot Snapshot)> OpenedSession(int fileCount = 8)
    {
        var scratch = new ScratchFolder(fileCount);
        var (link, tap) = LinkFactory.Build(server);
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        return (link, tap, scratch, opened.Snapshot);
    }

    private static string? Field(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    [Fact]
    public async Task N1_LostResponseLanded()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.ForwardThenDropResponse);
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);

        var resync = Assert.IsType<ActionResult.Resynchronised>(result);
        Assert.Equal(ResyncReason.LandedEarlier, resync.Why);

        var bodies = tap.Sent.Where(s => s.Path == "/session/vote").Select(s => s.BodyText).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(1, resync.Snapshot.SessionVotes);
        Assert.Equal(1L, resync.Snapshot.PairSeq);
    }

    [Fact]
    public async Task N2_LostRequestNotLanded()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.DropBeforeForward, ScriptedFault.Forward);
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);

        Assert.IsType<ActionResult.Applied>(result);
        var bodies = tap.Sent.Where(s => s.Path == "/session/vote").Select(s => s.BodyText).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);

        // SERVER_SPEC.md § 13.1 / § 10.5: a vote's 200 means applied, and on disk within the bound;
        // POST /session/save is how a client asks for "on disk now" before reading the file.
        await link.SaveAsync();
        Assert.Equal(2, scratch.TotalMatches());
    }

    [Fact]
    public async Task N3_BothAttemptsLostThenTheOwnerPressesAgain()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;
        var t0 = snapshot.PairToken;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.DropBeforeForward, ScriptedFault.DropBeforeForward);
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);

        Assert.IsType<ActionResult.Unknown>(result);
        Assert.Equal(LinkState.InSessionUnreachable, link.State);
        Assert.Equal(t0, link.Snapshot!.PairToken);

        var firstTwo = tap.Sent.Where(s => s.Path == "/session/vote").ToList();
        Assert.Equal(2, firstTwo.Count);
        Assert.Equal(firstTwo[0].BodyText, firstTwo[1].BodyText);
        var firstRequestId = Field(firstTwo[0].BodyText, "clientRequestId");

        await Task.Delay(2000);
        Assert.Equal(2, tap.Sent.Count(s => s.Path == "/session/vote"));

        var again = await link.VoteAsync(Side.Left, 0);
        Assert.IsType<ActionResult.Applied>(again);

        var third = tap.Sent.Where(s => s.Path == "/session/vote").ToList()[2];
        Assert.Equal(t0, Field(third.BodyText, "pairToken"));
        Assert.NotEqual(firstRequestId, Field(third.BodyText, "clientRequestId"));

        // SERVER_SPEC.md § 13.1 / § 10.5: a vote's 200 means applied, and on disk within the bound;
        // POST /session/save is how a client asks for "on disk now" before reading the file.
        await link.SaveAsync();
        Assert.Equal(2, scratch.TotalMatches());
    }

    [Fact]
    public async Task N4_LandedBothRepliesLostThenRefreshThenVoteOnNewPair()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;
        var t0 = snapshot.PairToken;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.ForwardThenDropResponse, ScriptedFault.DropBeforeForward);
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        Assert.IsType<ActionResult.Unknown>(result);
        Assert.Equal(t0, link.Snapshot!.PairToken);

        var r0 = Field(tap.Sent.First(s => s.Path == "/session/vote").BodyText, "clientRequestId");

        var refreshed = await link.RefreshAsync();
        var applied1 = Assert.IsType<ActionResult.Applied>(refreshed);
        Assert.Equal(1L, applied1.Snapshot.PairSeq);
        Assert.Equal(r0, applied1.Snapshot.LastAction!.ClientRequestId);

        var second = await link.VoteAsync(Side.Right, 1);
        var applied2 = Assert.IsType<ActionResult.Applied>(second);
        Assert.Equal(2L, applied2.Snapshot.PairSeq);

        var voteRequests = tap.Sent.Where(s => s.Path == "/session/vote").ToList();
        Assert.Equal(3, voteRequests.Count);
        var lastVote = voteRequests[^1];
        Assert.NotEqual(t0, Field(lastVote.BodyText, "pairToken"));
        Assert.NotEqual(r0, Field(lastVote.BodyText, "clientRequestId"));

        // SERVER_SPEC.md § 13.1 / § 10.5: a vote's 200 means applied, and on disk within the bound;
        // POST /session/save is how a client asks for "on disk now" before reading the file.
        await link.SaveAsync();
        Assert.Equal(4, scratch.TotalMatches());
    }

    /// <summary>
    /// The plan's central claim: no public member of <see cref="RankMaster2.Pc.Link"/> (the wire
    /// mirror in <c>.Wire</c> is exempt) gives a caller a way to hand a <c>pairToken</c> in, or a way
    /// to ask for the wire-level <c>clientRequestId</c> back out — that is what makes a caller unable
    /// to re-send an action with a fresh token.
    /// <para/>
    /// Two narrowings from the plan's literal sentence ("no parameter name contains token or
    /// requestId, and no public method returns string"), both forced by the plan's own type
    /// definitions violating a maximally literal reading:
    /// <list type="bullet">
    /// <item><description>The banned substring for a parameter name is <c>token</c> or
    /// <c>clientrequestid</c>, not bare <c>requestid</c> — <see cref="Failure.RequestId"/> is § 4.1's
    /// own field, the server's <c>X-Request-Id</c> kept only for a log line, explicitly documented as
    /// safe to expose. Banning that substring outright would fail on the plan's own frozen seam.</description></item>
    /// <item><description>"No public method returns string" is checked only for a method whose own
    /// name looks like a token/id accessor, not for every string-returning method in the namespace —
    /// <see cref="Paths.DefaultCredentialDirectory"/> and <see cref="Paths.DefaultTrayExecutable"/>
    /// are plan-sanctioned (§ 3, § 4.3) public statics that return a filesystem path, not a
    /// token.</description></item>
    /// </list>
    /// Also excluded: compiler-synthesised record plumbing (<c>ToString</c>, <c>Equals</c>,
    /// <c>GetHashCode</c>, <c>Deconstruct</c>, equality operators, property accessors — a property's
    /// own name is not checked, only its accessor's parameters, since <c>Failure.RequestId</c> the
    /// *property* is exactly the sanctioned case above).
    /// </summary>
    [Fact]
    public void N5_NoPublicMemberOfLinkCarriesATokenOrRequestId()
    {
        var assembly = typeof(ISessionLink).Assembly;
        var linkTypes = assembly.GetTypes()
            .Where(t => t.Namespace == "RankMaster2.Pc.Link" && (t.IsPublic || t.IsNestedPublic))
            .ToList();

        Assert.NotEmpty(linkTypes);

        var offenders = new List<string>();
        var compilerGenerated = new HashSet<string>
        {
            "ToString", "Equals", "GetHashCode", "GetType", "Deconstruct",
            "op_Equality", "op_Inequality", "<Clone>$",
        };

        foreach (var type in linkTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (compilerGenerated.Contains(method.Name)) continue;
                if (method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal)))
                    continue; // property accessors — the property's own name is not the channel; its parameters (setters) still are, covered below

                foreach (var parameter in method.GetParameters())
                    if (LooksLikeAToken(parameter.Name))
                        offenders.Add($"{type.FullName}.{method.Name}(...{parameter.Name}...)");

                if (method.ReturnType == typeof(string) && LooksLikeAToken(method.Name))
                    offenders.Add($"{type.FullName}.{method.Name}() returns string");
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                foreach (var parameter in ctor.GetParameters())
                    if (LooksLikeAToken(parameter.Name))
                        offenders.Add($"{type.FullName}..ctor(...{parameter.Name}...)");
        }

        Assert.True(offenders.Count == 0, "Found a channel for a token/clientRequestId: " + string.Join("; ", offenders));

        static bool LooksLikeAToken(string? name) =>
            name is not null &&
            (name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("clientrequestid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task N6_KillMidVoteRestartReopen()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;
        var t0 = snapshot.PairToken;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.ForwardThenKillServer);
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        Assert.IsType<ActionResult.Unknown>(result);

        var r0 = Field(tap.Sent.First(s => s.Path == "/session/vote").BodyText, "clientRequestId");
        // ForwardThenKillServer's fault only covers the FIRST physical attempt; the retry-on-unreachable
        // still fires (a kill looks like a timeout) and its own send lands on a now-dead server, so two
        // requests are already recorded before the restart.
        var preRestartVoteCount = tap.Sent.Count(s => s.Path == "/session/vote");

        await server.RestartAsync();

        var refreshed = await link.RefreshAsync();
        var resync = Assert.IsType<ActionResult.Resynchronised>(refreshed);
        Assert.Equal(ResyncReason.SessionReplaced, resync.Why);
        Assert.Equal(0L, resync.Snapshot.PairSeq);
        Assert.Null(resync.Snapshot.LastAction);

        for (var i = 0; i < 5; i++)
        {
            var current = link.Snapshot!;
            Assert.NotNull(current.Pair);
            Assert.IsType<ActionResult.Applied>(await link.VoteAsync(Side.Left, current.PairSeq));
        }

        // SERVER_SPEC.md § 13.4, and the owner's trade made visible. Five votes landed after the
        // restart and every one of them is on disk once asked for. The sixth — the one that landed
        // microseconds before the server was SIGKILLed — is **not**: it was applied in memory and
        // answered, and the bounded write-behind had neither reached SaveDelaySeconds nor
        // MaxUnsavedChoices when the process died. § 13.4: the ratings survive a restart "except the
        // last ≤ 5 choices or ≤ 2 seconds of voting, which the owner accepted explicitly"
        // (2026-09-17: "I'm not afraid of losing a couple of votes, it's non-consequential").
        //
        // A SIGKILL is the only way to lose one: a clean shutdown flushes (§ 13.1), which is why
        // this test kills rather than stops. With RankMaster2:SaveDelaySeconds = 0 the count here
        // would be 12, and that is what the switch is for.
        await link.SaveAsync();
        Assert.Equal(10, scratch.TotalMatches());

        var postRestart = tap.Sent.Where(s => s.Path == "/session/vote").Skip(preRestartVoteCount);
        foreach (var b in postRestart)
        {
            Assert.NotEqual(t0, Field(b.BodyText, "pairToken"));
            Assert.NotEqual(r0, Field(b.BodyText, "clientRequestId"));
        }
    }

    [Fact]
    public async Task N7_DiscardReplayed()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;
        var leftId = snapshot.Pair!.Left.Id;
        var filesBefore = scratch.Names.Count;

        tap.Script(HttpMethod.Post, "/session/discard", ScriptedFault.ForwardThenDropResponse);
        var result = await link.DiscardAsync(Side.Left, snapshot.PairSeq);
        var resync = Assert.IsType<ActionResult.Resynchronised>(result);
        Assert.Equal(ResyncReason.LandedEarlier, resync.Why);

        Assert.True(scratch.InDiscarded(leftId));
        var remaining = Directory.GetFiles(scratch.Path, "*.png", SearchOption.TopDirectoryOnly).Length;
        Assert.Equal(filesBefore - 1, remaining);

        var bodies = tap.Sent.Where(s => s.Path == "/session/discard").Select(s => s.BodyText).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
    }

    [Fact]
    public async Task N8_UndoReplayed()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var voted = Assert.IsType<ActionResult.Applied>(await link.VoteAsync(Side.Left, snapshot.PairSeq));
        Assert.Equal(1, voted.Snapshot.SessionVotes);

        tap.Script(HttpMethod.Post, "/session/undo", ScriptedFault.ForwardThenDropResponse);
        var result = await link.UndoAsync();
        var resync = Assert.IsType<ActionResult.Resynchronised>(result);
        Assert.Equal(ResyncReason.UndoAlreadyDone, resync.Why);
        Assert.Equal(0, resync.Snapshot.SessionVotes);
        Assert.Equal(2L, resync.Snapshot.PairSeq);
    }

    [Fact]
    public async Task N9_TokenRevokedMidSession()
    {
        var credentialDir = Path.Combine(Path.GetTempPath(), "rm2-link-cred", Guid.NewGuid().ToString("N"));
        using var scratch = new ScratchFolder(8);
        var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        var snapshot = opened.Snapshot;

        var credentialBefore = Credential.Load(credentialDir)!;

        // The owner revokes this device from his own PC. This used to pair a second device and have
        // it unpair the first, which the server now refuses: over HTTP a device may revoke only
        // itself (AUDIT2.md § 3.13). What the test is about — a token that stops working mid-session —
        // is unchanged; only the way the owner does it is.
        await server.RevokeDeviceAsOwnerAsync(credentialBefore.DeviceId);

        tap.ClearSent();
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        Assert.IsType<ActionResult.Applied>(result);

        var voteRequests = tap.Sent.Where(s => s.Path == "/session/vote").ToList();
        Assert.Equal(2, voteRequests.Count);
        Assert.Equal(voteRequests[0].BodyText, voteRequests[1].BodyText);
        Assert.NotEqual(voteRequests[0].Bearer, voteRequests[1].Bearer);

        var credentialAfter = Credential.Load(credentialDir)!;
        Assert.NotEqual(credentialBefore.Token, credentialAfter.Token);

        // SERVER_SPEC.md § 13.1 / § 10.5: a vote's 200 means applied, and on disk within the bound;
        // POST /session/save is how a client asks for "on disk now" before reading the file.
        await link.SaveAsync();
        Assert.Equal(2, scratch.TotalMatches());
    }

    [Fact]
    public async Task N10_ThePhoneMovedThePair()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        // The phone moves the pair twice, so the link's still-held pairSeq-0 token is two
        // generations behind by the time it acts: neither lastAction.pairToken nor
        // lastAction.clientRequestId can match what the link is about to send, which is the only
        // way § 8.5's table actually reaches PairMovedElsewhere. A single phone action would leave
        // lastAction.pairToken exactly equal to the link's stale token (both derived from the same
        // pairSeq-0 generation) and classify as TokenSpentByOwnEarlierRequest instead — indistinguishable
        // from the link's own retry, because a pairToken carries no caller identity (§ 8.1).
        var phone = await server.PairSecondDeviceAsync("n10-phone");
        using var phoneHttp = phone.CreateClient();

        var voteBody = new StringContent(
            JsonSerializer.Serialize(new { pairToken = snapshot.PairToken, winner = "left", clientRequestId = "phone-1" }),
            System.Text.Encoding.UTF8, "application/json");
        var phoneVote = await phoneHttp.PostAsync(server.BaseUrl + "/session/vote", voteBody);
        phoneVote.EnsureSuccessStatusCode();
        var afterVote = JsonSerializer.Deserialize(await phoneVote.Content.ReadAsByteArrayAsync(), WireJsonContext.Default.Snapshot)!;

        var skipBody = new StringContent(
            JsonSerializer.Serialize(new { pairToken = afterVote.PairToken, clientRequestId = "phone-2" }),
            System.Text.Encoding.UTF8, "application/json");
        var phoneSkip = await phoneHttp.PostAsync(server.BaseUrl + "/session/skip", skipBody);
        phoneSkip.EnsureSuccessStatusCode();

        tap.ClearSent();
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var resync = Assert.IsType<ActionResult.Resynchronised>(result);
        Assert.Equal(ResyncReason.PairMovedElsewhere, resync.Why);
        Assert.Equal(2L, link.Snapshot!.PairSeq);

        // SERVER_SPEC.md § 13.1 / § 10.5: a vote's 200 means applied, and on disk within the bound;
        // POST /session/save is how a client asks for "on disk now" before reading the file.
        await link.SaveAsync();
        Assert.Equal(2, scratch.TotalMatches()); // one vote (the phone's) on disk; the skip changes no rating

        tap.ClearSent();
        var again = await link.VoteAsync(Side.Left, 0);
        Assert.Equal(NotSentReason.PairMoved, Assert.IsType<ActionResult.NotSent>(again).Why);
        Assert.Empty(tap.Sent);
    }

    [Fact]
    public async Task N11_PinMismatchNeverRetried()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.Script(HttpMethod.Post, "/session/vote", ScriptedFault.ThrowPinMismatch);
        tap.ClearSent();
        var result = await link.VoteAsync(Side.Left, snapshot.PairSeq);
        var unknown = Assert.IsType<ActionResult.Unknown>(result);
        Assert.Equal(FailureKind.NotYourServer, unknown.Failure.Kind);
        Assert.True(unknown.Failure.Fatal);
        Assert.Single(tap.Sent.Where(s => s.Path == "/session/vote"));
        Assert.Equal(LinkState.Disconnected, link.State);
    }

    [Fact]
    public async Task N12_PostPairNeverRetried()
    {
        var credentialDir = Path.Combine(Path.GetTempPath(), "rm2-link-cred", Guid.NewGuid().ToString("N"));
        var (link, tap) = LinkFactory.Build(server, LinkFactory.DefaultOptions(server, credentialDir));
        await using var linkScope = link;

        tap.Script(HttpMethod.Post, "/pair", ScriptedFault.ForwardThenDropResponse);
        var result = await link.ConnectAsync();
        var failed = Assert.IsType<ConnectResult.Failed>(result);
        Assert.Equal(FailureKind.PairingFailed, failed.Failure.Kind);
        Assert.Single(tap.Sent.Where(s => s.Path == "/pair"));

        tap.ClearScripts();
        var second = await link.ConnectAsync();
        Assert.IsType<ConnectResult.Connected>(second);
    }

    [Fact]
    public async Task N13_NoBackgroundActivity()
    {
        var (link, tap, scratch, _) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        tap.ClearSent();
        await Task.Delay(TimeSpan.FromSeconds(15));
        Assert.Empty(tap.Sent);
    }

    [Fact]
    public async Task N14_PairSeqNeverOnTheWire()
    {
        var (link, tap, scratch, snapshot) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var voted = Assert.IsType<ActionResult.Applied>(await link.VoteAsync(Side.Left, snapshot.PairSeq));
        var skipped = Assert.IsType<ActionResult.Applied>(await link.SkipAsync(voted.Snapshot.PairSeq));
        var discarded = Assert.IsType<ActionResult.Applied>(await link.DiscardAsync(Side.Left, skipped.Snapshot.PairSeq));
        var specialed = Assert.IsType<ActionResult.Applied>(await link.SpecialAsync(Side.Right, discarded.Snapshot.PairSeq));
        await link.UndoAsync();
        await link.SaveAsync();
        _ = specialed;

        var actionBodies = tap.Sent
            .Where(s => s.Path.StartsWith("/session/", StringComparison.Ordinal) && s.Body.Length > 0)
            .Select(s => s.BodyText);

        foreach (var body in actionBodies)
        {
            using var document = JsonDocument.Parse(body);
            Assert.False(document.RootElement.TryGetProperty("pairSeq", out _), $"body carried pairSeq: {body}");
        }
    }
}
