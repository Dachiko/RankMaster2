using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>
/// The contract's own answers to the questions the independent audit asked of it: what a wrong
/// method on a real route produces, whether a media error carries a session block, whether the
/// rename route reads its body, and whether a rename is accepted once the folder is exhausted.
///
/// <para>These began as throwaway evidence. Two of them found real defects (an undo that reached
/// past a <c>drop_missing</c>, a rename that answered 202 to a malformed body), which are fixed;
/// the others record what SERVER_SPEC.md now says on purpose, so that a change of mind has to be a
/// change of the document first.</para>
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public sealed class ContractConformanceTests(Rm2Server server, ITestOutputHelper output) : SessionTestBase(server)
{
    /// <summary>
    /// AUDIT.md H5, reached three times independently. discard → vote → the left file vanishes from
    /// disk → discard that side (a <c>drop_missing</c>) → undo. SERVER_SPEC.md § 10.10: undo is one
    /// level and <c>drop_missing</c> is not cancellable, so nothing older may be reached. The server
    /// used to answer 200 and move the file discarded three actions earlier back out of
    /// <c>discarded/</c> — a file the owner had put there on purpose, and a pair change nobody asked
    /// for.
    /// </summary>
    [Fact]
    public async Task Undo_after_drop_missing_does_not_reach_back_to_an_older_discard()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var discardedId = opened.Left.Id;
        var afterDiscard = (await client.DiscardAsync(opened.RequireToken("open"), "left", "d1"))
            .ShouldBeSnapshot(200, "discard");
        Assert.True(afterDiscard.UndoAvailable);
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", discardedId)));

        var afterVote = (await client.VoteAsync(afterDiscard.RequireToken("after discard"), "left", "v1"))
            .ShouldBeSnapshot(200, "vote");
        Assert.True(afterVote.UndoAvailable, "the vote is cancellable");

        // Delete the current left file out from under the session, then discard that side.
        var vanished = afterVote.Left.Id;
        File.Delete(folder.File(vanished));
        var afterDrop = (await client.DiscardAsync(afterVote.RequireToken("after vote"), "left", "d2"))
            .ShouldBeSnapshot(200, "discard of a vanished file");
        Assert.Equal("drop_missing", afterDrop.RequireLastAction("drop").Type);

        Assert.False(afterDrop.UndoAvailable,
            "SERVER_SPEC.md § 9.1 and § 10.10: drop_missing is not cancellable and it does not hand " +
            "the cancel on to whatever came before it, so the snapshot must not offer one (AUDIT.md A7).");

        var undo = await client.UndoAsync("u1");
        undo.ShouldBeError("nothing_to_undo",
            "SERVER_SPEC.md § 10.10: one level; drop_missing is not cancellable and the vote before it " +
            "is no longer cancellable, so nothing older may be reached");

        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", discardedId)),
            $"'{discardedId}' was discarded three actions ago, on purpose. It must still be in " +
            "discarded/ (AUDIT.md H5).");
    }

    /// <summary>
    /// SERVER_SPEC.md § 4 and § 11.3: a media error carries <c>error.session: null</c>. The session
    /// block is attached by the session layer, which holds the gate; the media layer is lock-free by
    /// design (§ 11.3) and must stay that way, so it attaches none. Building a lock-free snapshot
    /// for a 404 that no client reads would be cost without value, and the contract says so rather
    /// than leaving the difference to be discovered.
    /// </summary>
    [Fact]
    public async Task Media_errors_carry_a_null_error_session_even_while_a_session_is_open()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        var missing = await client.MetaAsync("no-such-file.jpg");
        var failure = missing.ShouldBeError("unknown_media_id", "unknown id");
        output.WriteLine("media 404 body: " + missing.Text);

        Assert.Null(failure.Session);
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.16 and openapi.yaml: <c>POST /session/rename</c>'s body is optional, and a
    /// pairToken in it is ignored — but "ignored" is not "never read". A body that is present and
    /// malformed is a 400 like every other POST here. The route used to skip the body entirely and
    /// answer 202 to <c>{ not json</c>, which taught a client that this one endpoint has its own
    /// rules (AUDIT.md C9).
    /// </summary>
    [Fact]
    public async Task Rename_start_with_a_malformed_json_body_is_refused()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/session/rename", "{ not json");
        output.WriteLine($"POST /session/rename with malformed JSON -> {response.StatusCode} {response.ErrorCode}");

        if (response.StatusCode == 202)
            await RenamePolling.PollToTerminalAsync(client);

        Assert.Equal(400, response.StatusCode);

        // § 5.2's code table and § 10.16's prose now agree: a body that is not valid JSON is
        // `invalid_request` with `details.field`. (The prose said `invalid_json` for one commit,
        // a code that has never existed in the table; the table won and the prose was corrected.)
        Assert.Equal("invalid_request", response.ErrorCode);

        // An empty body and {} still mean the same thing and still start a rename.
        (await client.SendAsync(HttpMethod.Post, "/session/rename", new { }))
            .ShouldHaveStatus(202, "SERVER_SPEC.md § 10.16: an empty object is a valid rename request");
        await RenamePolling.PollToTerminalAsync(client);
    }

    /// <summary>
    /// SERVER_SPEC.md § 1.1 and § 5.2: 405 is not in the code table. A wrong method on a known path
    /// falls through to <c>404 not_found</c> in the one envelope this API has, which is what the
    /// contract now says outright.
    /// </summary>
    [Fact]
    public async Task A_wrong_method_on_a_real_route_is_not_found()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Put, "/session", new { folder = "/x" });
        output.WriteLine($"PUT /session -> {response.StatusCode} {response.ErrorCode}; Allow={response.HeaderOrNull("Allow")}");

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("not_found", response.ErrorCode);
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.16: a rename is not pair-scoped, so an exhausted session — every file
    /// discarded but one — starts one just the same. Nothing about ranking is required for renaming
    /// by rank.
    /// </summary>
    [Fact]
    public async Task Rename_in_the_exhausted_state_is_accepted()
    {
        using var folder = LibraryFolder.TwoStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var discarded = (await client.DiscardAsync(opened.RequireToken("open"), "left"))
            .ShouldBeSnapshot(200, "discard");
        Assert.True(discarded.IsExhausted);

        (await client.StartRenameAsync()).ShouldHaveStatus(
            202, "SERVER_SPEC.md § 10.16: rename takes no pairToken, so an exhausted session may start one");

        var final = await RenamePolling.PollToTerminalAsync(client);
        Assert.Equal("succeeded", final.GetProperty("state").GetString());

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after");
        output.WriteLine($"after: state={after.State} total={after.Total} pairSeq={after.PairSeq}");
        Assert.True(after.IsExhausted, "one file cannot make a pair; the rename does not change that");
        Assert.Equal("rename", after.RequireLastAction("§ 9.4").Type);
    }
}
