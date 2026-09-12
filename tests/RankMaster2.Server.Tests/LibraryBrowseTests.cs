using System.Text.Json;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// `GET /libraries/roots` and `GET /libraries/browse` (SERVER_SPEC.md § 10.14, § 10.15).
///
/// Browsing is unrestricted by decision, which makes the `rankable` predicate the interesting part:
/// § 10.15 promises it is "the same predicate POST /session applies", so a client can grey out a
/// folder rather than discovering at open time that it answers 409.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class LibraryBrowseTests(Rm2Server server) : SessionTestBase(server)
{
    [Fact]
    public async Task Roots_lists_mount_points_in_the_contract_shape()
    {
        var client = await ClientAsync();
        var response = await client.RootsAsync();
        response.ShouldHaveStatus(200, "GET /libraries/roots (SERVER_SPEC.md § 10.14)");

        var body = response.JsonBody;
        Assert.True(body.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Array,
            "SERVER_SPEC.md § 10.14: the response is { roots: [RootEntry] }.");
        Assert.True(roots.GetArrayLength() > 0, "There is at least one mount point on any running machine.");

        foreach (var root in roots.EnumerateArray())
        {
            ContractShape.RequireExactKeys(root, "RootEntry", ContractShape.RootEntryKeys,
                                           "GET /libraries/roots (SERVER_SPEC.md § 10.14)", response);

            var kind = root.GetProperty("kind").GetString();
            Assert.True(kind is "fixed" or "removable" or "network" or "ram" or "unknown",
                $"SERVER_SPEC.md § 10.14: kind is one of fixed|removable|network|ram|unknown. Got '{kind}'.");
        }
    }

    [Fact]
    public async Task Browse_lists_child_folders_and_counts_their_media()
    {
        // A parent holding three children: one rankable, one with a single file, one with none.
        var parent = Path.Combine(Path.GetTempPath(), "rm2-tests", $"browse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);

        try
        {
            var rankable = Path.Combine(parent, "rankable");
            var lonely = Path.Combine(parent, "one file");
            var barren = Path.Combine(parent, "empty");
            foreach (var path in new[] { rankable, lonely, barren }) Directory.CreateDirectory(path);

            await File.WriteAllBytesAsync(Path.Combine(rankable, "a.jpg"), MediaFixtures.Jpeg(64, 48));
            await File.WriteAllBytesAsync(Path.Combine(rankable, "b.jpg"), MediaFixtures.Jpeg(64, 48));
            await File.WriteAllBytesAsync(Path.Combine(rankable, "c.avi"), MediaFixtures.SmallVideo());
            await File.WriteAllTextAsync(Path.Combine(rankable, "rankmaster_db.json"), "{\"images\":{}}");
            await File.WriteAllBytesAsync(Path.Combine(lonely, "only.jpg"), MediaFixtures.Jpeg(64, 48));
            await File.WriteAllTextAsync(Path.Combine(barren, "readme.txt"), "no media here");

            var client = await ClientAsync();
            var response = await client.BrowseAsync(parent);
            response.ShouldHaveStatus(200, "GET /libraries/browse (SERVER_SPEC.md § 10.15)");

            var body = response.JsonBody;
            ContractShape.RequireExactKeys(body, "BrowseResponse", ContractShape.BrowseResponseKeys,
                                           "GET /libraries/browse", response);

            var entries = body.GetProperty("entries").EnumerateArray().ToArray();
            foreach (var entry in entries)
                ContractShape.RequireExactKeys(entry, "BrowseEntry", ContractShape.BrowseEntryKeys,
                                               "GET /libraries/browse", response);

            Assert.Equal(3, entries.Length);

            var byName = entries.ToDictionary(e => e.GetProperty("name").GetString()!);

            // § 10.15: counts are top-level files of that child only, applying SPEC.md § Media policy.
            Assert.Equal(2, byName["rankable"].GetProperty("stillCount").GetInt32());
            Assert.Equal(1, byName["rankable"].GetProperty("videoCount").GetInt32());
            Assert.True(byName["rankable"].GetProperty("hasDatabase").GetBoolean(),
                "SERVER_SPEC.md § 10.15: hasDatabase is true when rankmaster_db.json exists there.");

            // § 10.15: rankable is "the same predicate POST /session applies". A mixed folder ranks
            // stills only, so two stills is enough.
            Assert.True(byName["rankable"].GetProperty("rankable").GetBoolean(),
                "SERVER_SPEC.md § 10.15: two stills is rankable under the mixed-folder rule.");
            Assert.False(byName["one file"].GetProperty("rankable").GetBoolean(),
                "SERVER_SPEC.md § 10.15: one file would answer 409 folder_not_rankable, so rankable is false.");
            Assert.False(byName["empty"].GetProperty("rankable").GetBoolean());

            Assert.Equal(0, byName["empty"].GetProperty("stillCount").GetInt32());
            Assert.False(byName["empty"].GetProperty("hasDatabase").GetBoolean());

            Assert.All(entries, entry =>
                Assert.True(entry.GetProperty("accessible").GetBoolean(),
                    "Every child here is readable."));

            // § 10.15: "Files are never listed."
            Assert.DoesNotContain(entries, e => e.GetProperty("name").GetString() == "readme.txt");

            // The promise is testable directly: open the folder browse said was rankable.
            var opened = await client.OpenSessionAsync(rankable);
            opened.ShouldBeSnapshot(201,
                "SERVER_SPEC.md § 10.15: `rankable` is the same predicate POST /session applies, so a folder " +
                "browse reported as rankable must actually open");
            await client.CloseSessionAsync();

            var refused = await client.OpenSessionAsync(lonely);
            refused.ShouldBeError("folder_not_rankable",
                "SERVER_SPEC.md § 10.15: and a folder browse reported as not rankable must answer 409");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Counts_false_nulls_every_count_rather_than_zeroing_it()
    {
        var parent = Path.Combine(Path.GetTempPath(), "rm2-tests", $"browse-nocount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(parent, "child"));

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(parent, "child", "a.jpg"), MediaFixtures.Jpeg(32, 32));

            var client = await ClientAsync();
            var response = await client.BrowseAsync(parent, counts: false);
            response.ShouldHaveStatus(200, "GET /libraries/browse?counts=false");

            var entry = response.JsonBody.GetProperty("entries").EnumerateArray().Single();

            // § 10.15: "With counts=false, every count is null and rankable is null." The difference
            // from zero is the whole point — null means unknown, 0 means counted and empty.
            foreach (var field in new[] { "stillCount", "videoCount", "rankable" })
                Assert.True(entry.GetProperty(field).ValueKind == JsonValueKind.Null,
                    $"SERVER_SPEC.md § 10.15: with counts=false, {field} is null — not zero. " +
                    $"Got {entry.GetProperty(field)}.");

            // hasDatabase is a single File.Exists, so it survives counts=false.
            Assert.Equal(JsonValueKind.False, entry.GetProperty("hasDatabase").ValueKind);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Browse_reports_a_parent_and_never_recurses()
    {
        var parent = Path.Combine(Path.GetTempPath(), "rm2-tests", $"browse-depth-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(Path.Combine(child, "grandchild"));

        try
        {
            var client = await ClientAsync();
            var response = await client.BrowseAsync(parent);
            response.ShouldHaveStatus(200, "GET /libraries/browse");

            var entries = response.JsonBody.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Single(entries);
            Assert.Equal("child", entries[0].GetProperty("name").GetString());

            // § 10.15: "Direct child folders of path, never recursive."
            Assert.DoesNotContain(entries, e => e.GetProperty("name").GetString() == "grandchild");

            var reportedParent = response.JsonBody.GetProperty("parent");
            Assert.True(reportedParent.ValueKind == JsonValueKind.String,
                "SERVER_SPEC.md § 10.15: `parent` is null only at a root, and this folder has one.");
            Assert.Equal(Path.GetFullPath(Path.Combine(parent, "..")).TrimEnd(Path.DirectorySeparatorChar),
                         reportedParent.GetString()!.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    public async Task Browsing_a_path_that_is_not_absolute_is_refused(string path)
    {
        var client = await ClientAsync();
        var response = await client.BrowseAsync(path);

        response.ShouldBeErrorOneOf(
            $"SERVER_SPEC.md § 10.15: `path` is an absolute folder path; '{path}' is not",
            "invalid_path", "missing_field");
    }

    [Fact]
    public async Task Browsing_a_folder_that_does_not_exist_is_a_not_found()
    {
        var client = await ClientAsync();
        var missing = Path.Combine(Path.GetTempPath(), $"rm2-absent-{Guid.NewGuid():N}");

        var response = await client.BrowseAsync(missing);
        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 5.3: browsing a folder that does not exist is 404 folder_not_found",
            "folder_not_found", "not_found");
    }

    [Fact]
    public async Task Browsing_serves_no_file_bytes()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var response = await client.BrowseAsync(folder.Path);
        response.ShouldHaveStatus(200, "GET /libraries/browse on a folder full of files");

        // § 10.15: "Byte-serving is not [unrestricted]: nothing here returns file contents, and
        // /media/* serves only from inside the open session folder." Six media files are in this
        // folder and none of them may appear.
        Assert.Empty(response.JsonBody.GetProperty("entries").EnumerateArray());
    }
}
