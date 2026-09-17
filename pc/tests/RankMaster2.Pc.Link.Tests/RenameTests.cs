using System.Text.Json;
using System.Text.RegularExpressions;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Tests.Fixtures;
using RankMaster2.Pc.Link.Tests.Harness;
using RankMaster2.Pc.Link.Wire;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>
/// SERVER_SPEC.md § 10.16 and PC-RENAME's <c>ISessionLink</c> seam (§ 3.13): <c>StartRenameAsync</c>,
/// <c>GetRenameAsync</c>, <c>CancelRenameAsync</c> against the real server.
/// <para/>
/// § 3.13 asks for "cancel while the test's blocking <c>ICatalog</c> holds the run" — that
/// determinism seam (<c>GateCatalog</c>/<c>RenameGateServer</c>, a DI override inside a
/// <c>WebApplicationFactory&lt;Program&gt;</c>) lives entirely in
/// <c>tests/RankMaster2.Server.Tests/RenameTests.cs</c>, which S-SESSIONS owns (§ 3.9's Owns list)
/// and which is outside this package's boundary (Link/**, Ui/**) — § 0.1 rule 1 forbids reaching
/// into a file another package owns, and this project deliberately never references
/// <c>RankMaster2.Server</c> at all (only the real, out-of-process Kestrel server through
/// <see cref="RealServer"/>, § 6.2). It is also not reproducible with a plain file lock on this box:
/// <c>FileShare.None</c> does not block <c>File.Move</c> on Linux (verified — <c>rename(2)</c> is
/// unaffected by any process's open file descriptors, unlike Windows's sharing violation the retry
/// loop exists for), so there is no cross-platform way here to force <c>FileOps.MoveWithRetry</c>
/// into its retry branch from outside the process. <see cref="Cancel_issued_immediately_after_start_never_loses_a_rating_either_way"/>
/// below races a real cancel against a real run instead, and asserts the safety invariant that holds
/// whichever side of the race wins — proving cancel is never destructive here. The specific "lands
/// within one retry interval on a locked file" latency bound can only be shown on the owner's own
/// slow drive (§ 6) or by S-SESSIONS's own gated suite.
/// </summary>
[Collection("RealServer")]
public sealed class RenameTests(RealServer server)
{
    private static readonly Regex NamePattern = new(@"^\d{6}-[0-9a-f]{4}\.[^.]+$", RegexOptions.Compiled);

    private async Task<(SessionLink Link, ScratchFolder Scratch, Snapshot Snapshot)> OpenedSession(int fileCount = 6)
    {
        var scratch = new ScratchFolder(fileCount);
        var (link, _) = LinkFactory.Build(server);
        await link.ConnectAsync();
        var opened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        return (link, scratch, opened.Snapshot);
    }

    private static async Task<RenameOperation> PollToTerminal(SessionLink link, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var result = await link.GetRenameAsync();
            if (result is RenameOperationResult.Observed(var op) && op.IsTerminal)
                return op;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"GET /session/rename never reached a terminal state within {timeout}. Last: {Describe(result)}");

            await Task.Delay(20);
        }
    }

    /// <summary>Every top-level file the server would list as media, i.e. not
    /// <c>rankmaster_db.json</c>, its backups, <c>.rankmaster.lock</c> or the rename journal.</summary>
    private static List<string> MediaFileNames(string folder) =>
        Directory.GetFiles(folder)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n!.StartsWith(".", StringComparison.Ordinal)
                        && !n!.StartsWith("rankmaster_", StringComparison.Ordinal))
            .Select(n => n!)
            .ToList();

    private static string Describe(RenameOperationResult result) => result switch
    {
        RenameOperationResult.Observed(var op) => $"Observed(state={op.State}, phase={op.Phase}, done={op.Done}, total={op.Total})",
        RenameOperationResult.NoOperation => "NoOperation",
        RenameOperationResult.Refused(var f) => $"Refused({f.Code}: {f.Title})",
        _ => result.ToString() ?? "?",
    };

    [Fact]
    public async Task GetRename_before_any_rename_is_started_answers_NoOperation()
    {
        var (link, scratch, _) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var result = await link.GetRenameAsync();

        Assert.IsType<RenameOperationResult.NoOperation>(result);
    }

    [Fact]
    public async Task StartRename_answers_with_a_running_or_already_terminal_operation_that_has_an_id()
    {
        var (link, scratch, _) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var result = await link.StartRenameAsync();

        var observed = Assert.IsType<RenameOperationResult.Observed>(result);
        Assert.NotEmpty(observed.Operation.OperationId);
        Assert.NotEmpty(observed.Operation.StartedAt);
        // Six files on a local disk: "over before a human can react" (SERVER_SPEC.md § 10.16) — the
        // 202 itself may already carry a terminal state. Either way it must be one of the five.
        Assert.Contains(observed.Operation.State, new[] { "running", "cancelling", "succeeded", "cancelled", "failed" });

        await PollToTerminal(link, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_successful_rename_reaches_succeeded_with_done_equal_to_total_and_every_new_name_matching_the_suffix_pattern()
    {
        var (link, scratch, _) = await OpenedSession(8);
        await using var linkScope = link;
        using var scratchScope = scratch;

        var start = Assert.IsType<RenameOperationResult.Observed>(await link.StartRenameAsync());
        Assert.False(start.Operation.IsTerminal is false && start.Operation.Total == 0, "a running operation should already know its plan size");

        var final = await PollToTerminal(link, TimeSpan.FromSeconds(10));

        Assert.Equal("succeeded", final.State);
        Assert.Equal("done", final.Phase);
        Assert.Equal(final.Total, final.Done);
        Assert.Null(final.Error);

        var names = MediaFileNames(scratch.Path);

        Assert.Equal(8, names.Count);
        var suffixes = new HashSet<string>();
        foreach (var name in names)
        {
            Assert.True(NamePattern.IsMatch(name), $"'{name}' does not match ^\\d{{6}}-[0-9a-f]{{4}}\\.ext$");
            suffixes.Add(name[7..11]);
        }
        Assert.Single(suffixes); // one suffix per run (§ 1.1)

        // Byte-wise sort of the names is rank order (§ 1.1): the six-digit prefixes are exactly
        // 000001..000008 once sorted.
        var ranks = names.Select(n => n[..6]).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(Enumerable.Range(1, 8).Select(i => i.ToString("D6")), ranks);
    }

    [Fact]
    public async Task Cancel_after_the_run_has_already_succeeded_is_idempotent_and_just_reports_it()
    {
        var (link, scratch, _) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        await link.StartRenameAsync();
        var final = await PollToTerminal(link, TimeSpan.FromSeconds(10));
        Assert.Equal("succeeded", final.State);

        var cancelled = Assert.IsType<RenameOperationResult.Observed>(await link.CancelRenameAsync());
        Assert.Equal("succeeded", cancelled.Operation.State); // unchanged — too late, § 10.16
        Assert.Equal(final.OperationId, cancelled.Operation.OperationId);
    }

    [Fact]
    public async Task A_second_StartRename_while_one_is_already_recorded_answers_rename_in_progress_or_a_fresh_run()
    {
        // Six local files rename fast enough that this is a genuine race (§ above): either the
        // server still has the first run recorded (rename_in_progress, the interesting case) or it
        // already finished and a fresh POST is free to start a second one. Both are safe; assert the
        // session is never left broken either way.
        var (link, scratch, _) = await OpenedSession();
        await using var linkScope = link;
        using var scratchScope = scratch;

        var first = await link.StartRenameAsync();
        Assert.IsType<RenameOperationResult.Observed>(first);

        var second = await link.StartRenameAsync();
        switch (second)
        {
            case RenameOperationResult.Refused(var failure):
                Assert.Equal(Codes.RenameInProgress, failure.Code);
                break;
            case RenameOperationResult.Observed(var op):
                Assert.NotEmpty(op.OperationId);
                break;
            default:
                Assert.Fail($"Unexpected second-start result: {Describe(second)}");
                break;
        }

        await PollToTerminal(link, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Races a real cancel against a real run on a larger folder (150 files — enough that the move
    /// phase is not certain to be over before the cancel request lands, though on this box's local
    /// disk it very often still will be). Whichever side wins, the safety invariant SERVER_SPEC.md
    /// § 10.16 promises must hold: every file is still present under either its old or its new name,
    /// nothing is duplicated or missing, and the operation reaches a terminal state without hanging.
    /// This is the honest substitute for the plan's "blocking ICatalog" scenario — see the class
    /// doc comment for why that exact mechanism is out of this package's reach.
    /// </summary>
    [Fact]
    public async Task Cancel_issued_immediately_after_start_never_loses_a_rating_either_way()
    {
        var (link, scratch, _) = await OpenedSession(150);
        await using var linkScope = link;
        using var scratchScope = scratch;

        var originalNames = new HashSet<string>(scratch.Names, StringComparer.Ordinal);

        await link.StartRenameAsync();
        var cancelResult = await link.CancelRenameAsync();
        Assert.IsType<RenameOperationResult.Observed>(cancelResult);

        var final = await PollToTerminal(link, TimeSpan.FromSeconds(10));
        Assert.Contains(final.State, new[] { "succeeded", "cancelled" });
        Assert.Null(final.Error);

        var namesOnDisk = MediaFileNames(scratch.Path);

        Assert.Equal(150, namesOnDisk.Count); // never fewer, never duplicated
        foreach (var name in namesOnDisk)
        {
            var isOld = originalNames.Contains(name);
            var isNew = NamePattern.IsMatch(name);
            Assert.True(isOld || isNew, $"'{name}' is neither an original name nor a suffixed one — a file was corrupted");
        }

        // Reopen and confirm every rating is still reachable (the point of the journal, § 10.16):
        // matches/impressions default to 0 for an untouched library, but the record must exist under
        // whatever name the file now carries.
        var reopened = Assert.IsType<OpenResult.Opened>(await link.OpenAsync(scratch.Path));
        Assert.Equal(150, reopened.Snapshot.Counts.Total);
    }
}
