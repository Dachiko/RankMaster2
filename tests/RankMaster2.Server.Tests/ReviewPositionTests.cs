using System.Text.Json;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// The phone's review position (SERVER_SPEC.md § 10.17 <c>reviewPosition</c>, § 10.19
/// <c>PUT /session/review-position</c>): one small hidden file in the library folder. It is not an
/// action and not part of <c>rankmaster_db.json</c>, so the tests say as much about what it must not
/// touch as about what it stores.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class ReviewPositionTests(Rm2Server server) : SessionTestBase(server)
{
    private const string FileName = ".rankmaster_review.json";

    private static string? PositionOf(Rm2Response items)
    {
        items.ShouldHaveStatus(200, "GET /session/items");
        var json = items.Json ?? throw items.Failure("GET /session/items: the body is not JSON.");
        var value = json.GetProperty("reviewPosition");
        Assert.True(value.ValueKind is JsonValueKind.String or JsonValueKind.Null, "reviewPosition is string|null");
        return value.GetString();
    }

    private static string? PutBodyPosition(Rm2Response put, string clause)
    {
        put.ShouldHaveStatus(200, clause);
        var json = put.Json ?? throw put.Failure($"{clause}: the body is not JSON.");
        Assert.Equal(new[] { "reviewPosition" }, json.EnumerateObject().Select(p => p.Name).ToArray());
        return json.GetProperty("reviewPosition").GetString();
    }

    private static string[] IdsOf(Rm2Response items)
    {
        items.ShouldHaveStatus(200, "GET /session/items");
        return items.JsonBody.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!).ToArray();
    }

    [Fact]
    public async Task No_file_means_null()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.Null(PositionOf(await client.GetItemsAsync()));
        Assert.False(folder.Has(FileName), "reading must not create the file");
    }

    [Fact]
    public async Task A_put_is_read_back_by_items_and_lands_in_the_folder()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();
        var id = IdsOf(await client.GetItemsAsync())[2];

        Assert.Equal(id, PutBodyPosition(await client.PutReviewPositionAsync(id), "PUT review-position"));
        Assert.Equal(id, PositionOf(await client.GetItemsAsync()));

        Assert.True(folder.Has(FileName));
        using var stored = JsonDocument.Parse(File.ReadAllText(folder.File(FileName)));
        Assert.Equal(1, stored.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(id, stored.RootElement.GetProperty("current").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", stored.RootElement.GetProperty("updatedAt").GetString()!);
        Assert.Equal(3, stored.RootElement.EnumerateObject().Count());
        Assert.False(folder.Has(FileName + ".tmp"), "the temp file is gone after the swap");

        if (OperatingSystem.IsWindows())
            Assert.True((File.GetAttributes(folder.File(FileName)) & FileAttributes.Hidden) != 0, "hidden on Windows");

        // A second put replaces it.
        var other = IdsOf(await client.GetItemsAsync())[4];
        Assert.Equal(other, PutBodyPosition(await client.PutReviewPositionAsync(other), "second PUT"));
        Assert.Equal(other, PositionOf(await client.GetItemsAsync()));
        if (OperatingSystem.IsWindows())
            Assert.True((File.GetAttributes(folder.File(FileName)) & FileAttributes.Hidden) != 0, "still hidden after a replace");
    }

    [Fact]
    public async Task A_hidden_temp_file_left_by_a_crash_does_not_block_the_next_write()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();
        var id = IdsOf(await client.GetItemsAsync())[1];

        folder.WriteText(FileName + ".tmp", "{\"half\":");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(folder.File(FileName + ".tmp"), FileAttributes.Hidden);

        Assert.Equal(id, PutBodyPosition(await client.PutReviewPositionAsync(id), "PUT over a stale temp file"));
        Assert.Equal(id, PositionOf(await client.GetItemsAsync()));
        Assert.False(folder.Has(FileName + ".tmp"));
    }

    [Fact]
    public async Task Putting_null_deletes_the_file_and_is_idempotent()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.PutReviewPositionAsync("alpha.jpg")).ShouldHaveStatus(200, "set");
        Assert.True(folder.Has(FileName));

        Assert.Null(PutBodyPosition(await client.PutReviewPositionAsync(null), "PUT null"));
        Assert.False(folder.Has(FileName));
        Assert.Null(PositionOf(await client.GetItemsAsync()));

        Assert.Null(PutBodyPosition(await client.PutReviewPositionAsync(null), "PUT null with no file"));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{ \"version\": 2, \"current\": \"alpha.jpg\" }")]
    [InlineData("{ \"version\": \"1\", \"current\": \"alpha.jpg\" }")]
    [InlineData("{ \"current\": \"alpha.jpg\" }")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("{ \"version\": 1, \"current\": 7 }")]
    [InlineData("{ \"version\": 1, \"current\": \"\" }")]
    [InlineData("{ \"version\": 1, \"current\": \"..\\\\x.jpg\" }")]
    public async Task A_bad_file_reads_as_null_and_is_never_an_error(string content)
    {
        using var folder = LibraryFolder.SixStills();
        folder.WriteText(FileName, content);
        await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.Null(PositionOf(await client.GetItemsAsync()));

        // And a put simply replaces it.
        Assert.Equal("bravo.jpg", PutBodyPosition(await client.PutReviewPositionAsync("bravo.jpg"), "PUT over a bad file"));
        Assert.Equal("bravo.jpg", PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task A_binary_junk_file_reads_as_null()
    {
        using var folder = LibraryFolder.SixStills();
        folder.Write(FileName, new byte[] { 0xFF, 0xFE, 0x00, 0x80, 0x7B });
        await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.Null(PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task A_stored_id_is_returned_as_stored_even_when_it_is_not_a_record()
    {
        using var folder = LibraryFolder.SixStills();
        folder.WriteText(FileName, "{ \"version\": 1, \"current\": \"long-gone.jpg\", \"updatedAt\": \"2026-10-04T10:00:00.000Z\" }");
        await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.Equal("long-gone.jpg", PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task An_id_that_is_not_a_record_is_accepted_by_the_put()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.Equal("just-discarded.jpg", PutBodyPosition(await client.PutReviewPositionAsync("just-discarded.jpg"), "unknown id"));
        Assert.Equal("clip-not-here.mp4", PutBodyPosition(await client.PutReviewPositionAsync("clip-not-here.mp4"), "unknown video id"));
        Assert.Equal("clip-not-here.mp4", PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task Discarding_the_stored_item_leaves_the_stored_id_alone()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();
        var id = IdsOf(await client.GetItemsAsync())[1];

        (await client.PutReviewPositionAsync(id)).ShouldHaveStatus(200, "set");
        (await client.DiscardItemAsync(id)).ShouldBeSnapshot(200, "discard the stored item");

        Assert.Equal(id, PositionOf(await client.GetItemsAsync()));
        Assert.DoesNotContain(id, IdsOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task Escaping_and_non_media_ids_are_refused_with_the_discard_codes_and_nothing_is_written()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        foreach (var bad in new[] { "..\\x.jpg", "../x.jpg", "sub/x.jpg", "sub\\x.jpg", "/x.jpg", "..", "." })
        {
            var failure = (await client.PutReviewPositionAsync(bad)).ShouldBeError(
                "media_outside_session", $"id '{bad}' would leave the folder");
            Assert.Equal(bad, failure.Detail("id", "§ 5.5").GetString());
        }

        var ext = (await client.PutReviewPositionAsync("notes.txt"))
            .ShouldBeError("media_extension_not_allowed", "a non-media extension");
        Assert.Equal("txt", ext.Detail("extension", "§ 5.5").GetString());

        var empty = (await client.PutReviewPositionAsync(""))
            .ShouldBeError("invalid_media_id", "an empty id");
        Assert.Equal("empty", empty.Detail("reason", "§ 5.2").GetString());
        (await client.PutReviewPositionAsync("a\u0001b.jpg")).ShouldBeError("invalid_media_id", "a control character");
        (await client.PutReviewPositionAsync(new string('x', 252) + ".jpg")).ShouldBeError("invalid_media_id", "too long");

        Assert.False(folder.Has(FileName));
        Assert.Null(PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task A_bad_body_is_answered_like_the_other_actions()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.SendAsync(HttpMethod.Put, "/session/review-position", new { other = 1 }))
            .ShouldBeError("missing_field", "id absent");
        (await client.SendAsync(HttpMethod.Put, "/session/review-position", "{\"id\":42}"))
            .ShouldBeError("invalid_request", "id of the wrong type");
        (await client.SendAsync(HttpMethod.Put, "/session/review-position", "not json"))
            .ShouldBeError("invalid_request", "not JSON");
        Assert.False(folder.Has(FileName));
    }

    [Fact]
    public async Task It_is_not_an_action_and_leaves_the_snapshot_and_the_undo_alone()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        // Give it a lastAction and an armed undo to protect.
        var voted = (await client.VoteAsync(opened.RequireToken("open"), "left", "v-1")).ShouldBeSnapshot(200, "a vote");
        Assert.True(voted.UndoAvailable);
        var before = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "snapshot before");

        (await client.PutReviewPositionAsync("alpha.jpg")).ShouldHaveStatus(200, "set");
        (await client.PutReviewPositionAsync(null)).ShouldHaveStatus(200, "clear");
        (await client.PutReviewPositionAsync("bravo.jpg")).ShouldHaveStatus(200, "set again");

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "snapshot after");
        Assert.Equal(before.PairSeq, after.PairSeq);
        Assert.Equal(before.PairToken, after.PairToken);
        Assert.Equal(before.UndoAvailable, after.UndoAvailable);
        Assert.Equal(before.SessionVotes, after.SessionVotes);
        Assert.Equal(before.Json.GetProperty("lastAction").GetRawText(), after.Json.GetProperty("lastAction").GetRawText());
        Assert.Equal(before.Json.GetProperty("lastSavedAt").GetRawText(), after.Json.GetProperty("lastSavedAt").GetRawText());

        // The puts did not spend the undo they found: the vote is still undoable.
        (await client.UndoAsync()).ShouldBeSnapshot(200, "undo still works after the puts");
    }

    [Fact]
    public async Task A_put_does_not_write_rankmaster_db_json()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();
        var dbPath = folder.File("rankmaster_db.json");

        // Settle whatever the open itself wrote, then watch the file across the puts alone.
        await client.CloseSessionAsync();
        await OpenAsync(folder);
        var existed = File.Exists(dbPath);
        var bytes = existed ? File.ReadAllBytes(dbPath) : null;
        var stamp = existed ? File.GetLastWriteTimeUtc(dbPath) : default;

        (await client.PutReviewPositionAsync("alpha.jpg")).ShouldHaveStatus(200, "set");
        (await client.PutReviewPositionAsync(null)).ShouldHaveStatus(200, "clear");

        Assert.Equal(existed, File.Exists(dbPath));
        if (existed)
        {
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(dbPath));
            Assert.Equal(bytes, File.ReadAllBytes(dbPath));
        }
    }

    [Fact]
    public async Task With_no_session_it_is_no_session_even_with_a_bad_body()
    {
        var client = await ClientAsync();

        (await client.PutReviewPositionAsync("alpha.jpg")).ShouldBeError("no_session", "PUT while closed");
        (await client.PutReviewPositionAsync(null)).ShouldBeError("no_session", "PUT null while closed");
        (await client.SendAsync(HttpMethod.Put, "/session/review-position", "not json"))
            .ShouldBeError("no_session", "§ 8.4 step 1 before step 2");
    }

    [Fact]
    public async Task The_file_is_not_a_media_item_and_does_not_disturb_open_or_scan()
    {
        using var folder = LibraryFolder.SixStills();
        folder.WriteText(FileName, "{ \"version\": 1, \"current\": \"alpha.jpg\", \"updatedAt\": \"2026-10-04T10:00:00.000Z\" }");

        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        Assert.Equal(6, opened.Total);
        var ids = IdsOf(await client.GetItemsAsync());
        Assert.Equal(6, ids.Length);
        Assert.DoesNotContain(ids, i => i.Contains("rankmaster_review", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("alpha.jpg", PositionOf(await client.GetItemsAsync()));

        // A vote and a close force saves, which re-scan the folder: the file is still not a record
        // and is still there afterwards.
        (await client.VoteAsync(opened.RequireToken("open"), "left")).ShouldBeSnapshot(200, "vote with the file present");
        await client.CloseSessionAsync();
        var reopened = await OpenAsync(folder);
        Assert.Equal(6, reopened.Total);
        Assert.True(folder.Has(FileName), "closing and saving left the file where it was");
    }

    [Fact]
    public async Task The_position_survives_closing_and_reopening_the_session()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();
        var id = IdsOf(await client.GetItemsAsync())[3];

        (await client.PutReviewPositionAsync(id)).ShouldHaveStatus(200, "set");
        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "close");
        Assert.True(folder.Has(FileName), "closing leaves it in the folder");

        await OpenAsync(folder);
        Assert.Equal(id, PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task It_is_allowed_when_exhausted()
    {
        using var folder = LibraryFolder.Mixed();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.DiscardItemAsync("alpha.jpg")).ShouldBeSnapshot(200, "first discard");
        var exhausted = (await client.DiscardItemAsync("bravo.jpg")).ShouldBeSnapshot(200, "second discard");
        Assert.True(exhausted.IsExhausted);

        Assert.Equal("clip.avi", PutBodyPosition(await client.PutReviewPositionAsync("clip.avi"), "PUT while exhausted"));
        Assert.Equal("clip.avi", PositionOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task A_completed_rename_leaves_the_file_alone()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        (await client.PutReviewPositionAsync("alpha.jpg")).ShouldHaveStatus(200, "set");
        var before = File.ReadAllText(folder.File(FileName));

        (await client.StartRenameAsync()).ShouldHaveStatus(202, "start");
        var final = await RenamePolling.PollToTerminalAsync(client);
        Assert.Equal("succeeded", final.GetProperty("state").GetString());

        Assert.Equal(before, File.ReadAllText(folder.File(FileName)));
        // The stored name is the pre-rename id, which is no longer a record: returned as stored.
        Assert.Equal("alpha.jpg", PositionOf(await client.GetItemsAsync()));
        Assert.DoesNotContain("alpha.jpg", IdsOf(await client.GetItemsAsync()));
    }

    [Fact]
    public async Task A_write_that_cannot_happen_is_save_failed_with_nothing_changed()
    {
        using var folder = LibraryFolder.SixStills();
        await OpenAsync(folder);
        var client = await ClientAsync();

        // A directory where the file should go: reads see "no file", a write cannot replace it.
        Directory.CreateDirectory(folder.File(FileName));
        var before = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "snapshot before");

        var failure = (await client.PutReviewPositionAsync("alpha.jpg")).ShouldBeError("save_failed", "an unwritable position");
        Assert.False(failure.Detail("recordsChanged", "§ 5.4").GetBoolean());
        Assert.False(failure.Detail("fileMoved", "§ 5.4").GetBoolean());
        Assert.False(folder.Has(FileName + ".tmp"), "the failed attempt cleans its temp file");

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "snapshot after");
        Assert.Equal(before.PairSeq, after.PairSeq);
        Assert.Null(PositionOf(await client.GetItemsAsync()));
    }
}
