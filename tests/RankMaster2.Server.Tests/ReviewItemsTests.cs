using System.Text.Json;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// Review mode (SERVER_SPEC.md § 10.17, § 10.18): <c>GET /session/items</c> and
/// <c>POST /session/items/discard</c>. The discard is the pair-based one with the id supplied by the
/// client, so the tests concentrate on what is new — any record can be named, the pair survives or
/// is re-picked, an exhausted session still accepts it — and on what must NOT be new: the id rules,
/// the undo, the missing-file case.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class ReviewItemsTests(Rm2Server server) : SessionTestBase(server)
{
    private static (Snapshot Session, MediaRef[] Items) ParseItems(Rm2Response response, string clause)
    {
        response.ShouldHaveStatus(200, clause);
        var json = response.Json ?? throw response.Failure($"{clause}: the body is not JSON.");
        var keys = json.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "items", "reviewPosition", "session" }, keys);

        var session = json.GetProperty("session");
        ContractShape.RequireSnapshot(session, $"{clause} (SERVER_SPEC.md § 10.17: session is a § 9.1 snapshot)", response);

        var items = json.GetProperty("items").EnumerateArray().Select(e => new MediaRef(e)).ToArray();
        foreach (var item in items)
            ContractShape.RequireExactKeys(item.Json, "MediaRef", ContractShape.MediaRefKeys, $"{clause}: item {item.Id}", response);

        return (new Snapshot(session, response), items);
    }

    // ---- GET /session/items -----------------------------------------------------------------

    [Fact]
    public async Task Items_lists_every_record_of_both_kinds_in_id_order_with_the_snapshot()
    {
        using var folder = LibraryFolder.Mixed();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var (session, items) = ParseItems(await client.GetItemsAsync(), "GET /session/items");

        Assert.Equal(new[] { "alpha.jpg", "bravo.jpg", "charlie.jpg", "clip.avi" }, items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "still", "still", "still", "video" }, items.Select(i => i.Kind).ToArray());
        Assert.Equal(session.Total, items.Length);
        Assert.Equal(opened.SessionId, session.SessionId);
        Assert.Equal(opened.PairSeq, session.PairSeq);

        var still = items[0];
        Assert.NotNull(still.SizeBytes);
        Assert.NotNull(still.MediaVersion);
        Assert.Contains("?v=" + still.MediaVersion, still.StillLink);
        Assert.Contains("?v=" + still.MediaVersion, still.ThumbLink);
        Assert.Null(still.VideoLink);

        var video = items[3];
        Assert.Null(video.StillLink);
        Assert.Null(video.ThumbLink);
        Assert.Contains("/media/clip.avi/video?v=", video.VideoLink);

        // The links are the ones the pair would have carried: they serve.
        (await client.FollowAsync(still.ThumbLink!)).ShouldHaveStatus(200, "a thumb link from items");
    }

    [Fact]
    public async Task Items_is_a_pure_read()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var first = ParseItems(await client.GetItemsAsync(), "first read").Session;
        var second = ParseItems(await client.GetItemsAsync(), "second read").Session;

        Assert.Equal(opened.PairSeq, first.PairSeq);
        Assert.Equal(opened.PairSeq, second.PairSeq);
        Assert.Equal(opened.PairToken, second.PairToken);
        Assert.Equal(opened.SessionVotes, second.SessionVotes);
    }

    [Fact]
    public async Task Items_marks_a_vanished_file_with_null_size_and_still_lists_it()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        File.Delete(folder.File("delta.jpg"));
        var (_, items) = ParseItems(await client.GetItemsAsync(), "GET /session/items");

        var gone = Assert.Single(items, i => i.Id == "delta.jpg");
        Assert.Null(gone.SizeBytes);
        Assert.Null(gone.MediaVersion);
        Assert.Equal(6, items.Length);
    }

    [Fact]
    public async Task Items_without_a_session_is_no_session()
    {
        var client = await ClientAsync();
        (await client.GetItemsAsync()).ShouldBeError("no_session", "GET /session/items while closed");
    }

    [Fact]
    public async Task Both_routes_need_the_same_auth_as_every_session_route()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);

        (await Anonymous.SendAsync(HttpMethod.Get, "/session/items", authenticate: false))
            .ShouldBeError("unauthenticated", "GET /session/items without a token");
        (await Anonymous.SendAsync(HttpMethod.Post, "/session/items/discard", new { id = "alpha.jpg" }, authenticate: false))
            .ShouldBeError("unauthenticated", "POST /session/items/discard without a token");
        Assert.True(folder.Has("alpha.jpg"));
    }

    // ---- POST /session/items/discard --------------------------------------------------------

    /// <summary>A record that is neither in the current pair.</summary>
    private static string NotInPair(Snapshot s, MediaRef[] items) =>
        items.First(i => !s.PairIds.Contains(i.Id)).Id;

    [Fact]
    public async Task Discarding_an_item_outside_the_pair_moves_it_and_leaves_the_pair_alone()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var items = ParseItems(await client.GetItemsAsync(), "items").Items;
        var id = NotInPair(opened, items);

        var after = (await client.DiscardItemAsync(id, "r-1")).ShouldBeSnapshot(200, "discard by id");

        Assert.False(folder.Has(id));
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", id)));
        Assert.Equal(opened.PairSeq + 1, after.PairSeq);
        Assert.Equal(5, after.Total);
        Assert.Equal("ranking", after.State);
        Assert.Equal(opened.PairIds, after.PairIds);
        Assert.NotEqual(opened.PairToken, after.PairToken);
        Assert.DoesNotContain(id, after.WarmPairMembers.Select(m => m.Id));
        Assert.True(after.UndoAvailable);

        var last = after.RequireLastAction("§ 9.4");
        Assert.Equal("discard", last.Type);
        Assert.Equal(id, last.Id);
        Assert.Null(last.SideValue);
        Assert.Null(last.PairToken);
        Assert.Equal("r-1", last.ClientRequestId);
        Assert.Equal(after.PairSeq, last.Seq);

        // The rating travelled with the file (§ 10.8): the subfolder has its own database naming it.
        var db = File.ReadAllText(Path.Combine(folder.Path, "discarded", "rankmaster_db.json"));
        Assert.Contains(id, db);

        // Nothing counted as an impression or a vote.
        Assert.Equal(0, after.SessionVotes);

        // The list now omits it.
        var (_, remaining) = ParseItems(await client.GetItemsAsync(), "items after");
        Assert.DoesNotContain(id, remaining.Select(i => i.Id));
    }

    [Fact]
    public async Task Discarding_a_member_of_the_current_pair_gives_a_valid_pair_without_it()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var id = opened.Left.Id;

        var after = (await client.DiscardItemAsync(id)).ShouldBeSnapshot(200, "discard of a pair member");

        Assert.Equal("ranking", after.State);
        Assert.NotNull(after.PairToken);
        Assert.Equal(2, after.PairIds.Length);
        Assert.DoesNotContain(id, after.PairIds);
        Assert.DoesNotContain(id, after.WarmPairMembers.Select(m => m.Id));
        Assert.NotEqual(opened.PairToken, after.PairToken);

        // The token of the pair that was on screen is stale now (§ 8.5), as after any change.
        (await client.VoteAsync(opened.RequireToken("open"), "left"))
            .ShouldBeError("stale_pair_token", "a vote on the pair the discard replaced");
    }

    [Fact]
    public async Task Discarding_a_warm_pair_member_removes_that_warm_pair()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var warmOnly = opened.WarmPairMembers.Select(m => m.Id).FirstOrDefault(i => !opened.PairIds.Contains(i));
        Assert.True(warmOnly is not null, "six stills with prefetch 2 must have a warm pair to test with");

        var after = (await client.DiscardItemAsync(warmOnly!)).ShouldBeSnapshot(200, "discard of a warm member");

        Assert.DoesNotContain(warmOnly!, after.WarmPairMembers.Select(m => m.Id));
        Assert.Equal(opened.PairIds, after.PairIds);
    }

    [Fact]
    public async Task Discarding_works_while_exhausted_and_a_pair_discard_still_does_not()
    {
        // Three stills and a video: the video never ranks, so two discards of stills exhaust the
        // session while records remain (§ 7.4.7).
        using var folder = LibraryFolder.Mixed();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.DiscardItemAsync("alpha.jpg")).ShouldBeSnapshot(200, "first discard");
        var exhausted = (await client.DiscardItemAsync("bravo.jpg")).ShouldBeSnapshot(200, "second discard");
        Assert.True(exhausted.IsExhausted);
        Assert.Null(exhausted.PairToken);
        Assert.Equal(2, exhausted.Total);

        // Items still reads.
        var (_, items) = ParseItems(await client.GetItemsAsync(), "items while exhausted");
        Assert.Equal(new[] { "charlie.jpg", "clip.avi" }, items.Select(i => i.Id).ToArray());

        // A pair discard needs a pair, a by-id discard does not.
        (await client.DiscardAsync("anything", "left")).ShouldBeError("no_current_pair", "pair discard while exhausted");
        var after = (await client.DiscardItemAsync("clip.avi", "v-1")).ShouldBeSnapshot(200, "discard while exhausted");
        Assert.True(after.IsExhausted);
        Assert.Equal(1, after.Total);
        Assert.Equal(1, after.Stills);
        Assert.Equal(0, after.Videos);
        Assert.True(File.Exists(Path.Combine(folder.Path, "discarded", "clip.avi")));
        Assert.True(after.UndoAvailable);
    }

    [Fact]
    public async Task Undo_cancels_a_discard_by_id_exactly_like_a_pair_discard()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var items = ParseItems(await client.GetItemsAsync(), "items").Items;
        var id = NotInPair(opened, items);

        var discarded = (await client.DiscardItemAsync(id, "d-1")).ShouldBeSnapshot(200, "discard by id");
        var undone = (await client.UndoAsync("u-1")).ShouldBeSnapshot(200, "undo of a discard by id");

        Assert.True(folder.Has(id));
        Assert.False(File.Exists(Path.Combine(folder.Path, "discarded", id)));
        Assert.Equal(6, undone.Total);
        Assert.Equal(discarded.PairSeq + 1, undone.PairSeq);
        Assert.False(undone.UndoAvailable);

        var last = undone.RequireLastAction("§ 9.4");
        Assert.Equal("undo", last.Type);
        Assert.Equal("discard", last.UndoneType);
        Assert.Equal(id, last.Id);
        Assert.Equal(id, last.RestoredId);

        var (_, back) = ParseItems(await client.GetItemsAsync(), "items after undo");
        Assert.Contains(id, back.Select(i => i.Id));

        // One level: a second undo has nothing to take back.
        (await client.UndoAsync()).ShouldBeError("nothing_to_undo", "a second undo");
    }

    [Fact]
    public async Task Undo_of_a_discard_by_id_reports_restoredId_when_the_name_was_taken()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var items = ParseItems(await client.GetItemsAsync(), "items").Items;
        var id = NotInPair(opened, items);

        (await client.DiscardItemAsync(id)).ShouldBeSnapshot(200, "discard by id");

        // Someone puts another file under the same name while the original is in discarded/.
        File.WriteAllBytes(folder.File(id), MediaFixtures.Jpeg(64, 64));

        var undone = (await client.UndoAsync()).ShouldBeSnapshot(200, "undo");
        var last = undone.RequireLastAction("§ 9.4");
        Assert.Equal(id, last.Id);
        Assert.NotNull(last.RestoredId);
        Assert.NotEqual(id, last.RestoredId);
        Assert.True(folder.Has(last.RestoredId!));
    }

    [Fact]
    public async Task An_unknown_id_is_unknown_media_id_with_the_id_and_a_snapshot()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        var failure = (await client.DiscardItemAsync("nothing-like-this.jpg"))
            .ShouldBeError("unknown_media_id", "an id that is not a record");

        Assert.Equal("nothing-like-this.jpg", failure.Detail("id", "§ 5.5").GetString());
        Assert.NotNull(failure.Session);
    }

    [Fact]
    public async Task Retrying_a_landed_discard_with_the_same_client_request_id_changes_nothing()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var items = ParseItems(await client.GetItemsAsync(), "items").Items;
        var id = NotInPair(opened, items);

        var first = (await client.DiscardItemAsync(id, "same-id")).ShouldBeSnapshot(200, "first attempt");
        var retry = (await client.DiscardItemAsync(id, "same-id")).ShouldBeSnapshot(200, "the retry (§ 13.3)");

        Assert.Equal(first.PairSeq, retry.PairSeq);
        Assert.Equal(first.PairToken, retry.PairToken);
        Assert.Equal(5, retry.Total);
        Assert.Equal(first.LastSavedAt, retry.LastSavedAt);
        Assert.Equal("discard", retry.RequireLastAction("§ 9.4").Type);

        // Only a matching, non-null clientRequestId makes it a retry.
        (await client.DiscardItemAsync(id, "another-id")).ShouldBeError("unknown_media_id", "different clientRequestId");
        (await client.DiscardItemAsync(id)).ShouldBeError("unknown_media_id", "no clientRequestId");
    }

    [Fact]
    public async Task A_retry_of_a_drop_missing_is_recognised_too()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var items = ParseItems(await client.GetItemsAsync(), "items").Items;
        var id = NotInPair(opened, items);

        File.Delete(folder.File(id));
        var first = (await client.DiscardItemAsync(id, "gone-1")).ShouldBeSnapshot(200, "first attempt");
        Assert.Equal("drop_missing", first.RequireLastAction("§ 9.4").Type);

        var retry = (await client.DiscardItemAsync(id, "gone-1")).ShouldBeSnapshot(200, "retry");
        Assert.Equal(first.PairSeq, retry.PairSeq);
    }

    [Fact]
    public async Task A_vanished_file_is_dropped_with_no_undo_entry()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();
        var items = ParseItems(await client.GetItemsAsync(), "items").Items;
        var id = NotInPair(opened, items);

        File.Delete(folder.File(id));
        var after = (await client.DiscardItemAsync(id, "m-1")).ShouldBeSnapshot(200, "discard of a missing file");

        var last = after.RequireLastAction("§ 9.4");
        Assert.Equal("drop_missing", last.Type);
        Assert.Equal(id, last.Id);
        Assert.Null(last.SideValue);
        Assert.Null(last.PairToken);
        Assert.Equal("m-1", last.ClientRequestId);
        Assert.Equal(opened.PairSeq + 1, after.PairSeq);
        Assert.Equal(5, after.Total);
        Assert.False(after.UndoAvailable);
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "discarded")),
            "nothing was moved, so no discarded/ was made");

        (await client.UndoAsync()).ShouldBeError("nothing_to_undo", "drop_missing is not cancellable");
    }

    [Fact]
    public async Task An_id_that_could_leave_the_folder_is_refused_and_nothing_moves()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        // A real, media-named file one level above the session folder: the thing an escape would hit.
        var parent = Path.GetDirectoryName(folder.Path)!;
        var victim = Path.Combine(parent, $"victim-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(victim, MediaFixtures.Jpeg(32, 32));
        var victimName = Path.GetFileName(victim);

        try
        {
            var escapes = new[]
            {
                "..\\" + victimName, "../" + victimName, "sub/" + victimName, "sub\\" + victimName,
                "/" + victimName, "..", ".", Path.Combine(parent, victimName),
            };
            foreach (var bad in escapes)
            {
                var failure = (await client.DiscardItemAsync(bad)).ShouldBeError(
                    "media_outside_session", $"id '{bad}' would leave the folder");
                Assert.Equal(bad, failure.Detail("id", "§ 5.5").GetString());
            }

            Assert.True(File.Exists(victim), "the file outside the folder must be untouched");
            Assert.False(Directory.Exists(Path.Combine(folder.Path, "discarded")));
            Assert.False(Directory.Exists(Path.Combine(parent, "discarded")));

            // Not a media extension, empty, control characters: the codes the media endpoints use.
            var ext = (await client.DiscardItemAsync("notes.txt"))
                .ShouldBeError("media_extension_not_allowed", "a non-media extension");
            Assert.Equal("txt", ext.Detail("extension", "§ 5.5").GetString());

            var empty = (await client.DiscardItemAsync(""))
                .ShouldBeError("invalid_media_id", "an empty id");
            Assert.Equal("empty", empty.Detail("reason", "§ 5.2").GetString());
            (await client.DiscardItemAsync("a\u0001b.jpg")).ShouldBeError("invalid_media_id", "a control character");
            (await client.DiscardItemAsync(new string('x', 252) + ".jpg")).ShouldBeError("invalid_media_id", "too long");

            // And the session is exactly as it was.
            Assert.Equal(6, ParseItems(await client.GetItemsAsync(), "items").Items.Length);
        }
        finally
        {
            File.Delete(victim);
        }
    }

    [Fact]
    public async Task A_bad_body_is_answered_like_the_other_actions()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.SendAsync(HttpMethod.Post, "/session/items/discard", new { clientRequestId = "x" }))
            .ShouldBeError("missing_field", "id absent");
        (await client.SendAsync(HttpMethod.Post, "/session/items/discard", "{\"id\":null}"))
            .ShouldBeError("missing_field", "id null");
        (await client.SendAsync(HttpMethod.Post, "/session/items/discard", "{\"id\":42}"))
            .ShouldBeError("invalid_request", "id of the wrong type");
        (await client.SendAsync(HttpMethod.Post, "/session/items/discard", "not json"))
            .ShouldBeError("invalid_request", "not JSON");
        (await client.DiscardItemAsync("alpha.jpg", new string('c', 65)))
            .ShouldBeError("invalid_request", "clientRequestId over 64 characters");

        Assert.True(folder.Has("alpha.jpg"));
    }

    [Fact]
    public async Task Discarding_without_a_session_is_no_session_even_with_a_bad_body()
    {
        var client = await ClientAsync();
        (await client.DiscardItemAsync("alpha.jpg")).ShouldBeError("no_session", "discard while closed");
        (await client.SendAsync(HttpMethod.Post, "/session/items/discard", "not json"))
            .ShouldBeError("no_session", "§ 8.4 step 1 before step 2");
    }
}
