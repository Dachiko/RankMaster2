using System.Text;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>
/// SERVER_SPEC.md § 11.2: "<c>/media/*</c> is the one place the token does not buy access to the
/// whole filesystem." Everything here tries to make that sentence false.
/// </summary>
public sealed class MediaEscapeTests(Rm2Server server) : AuditTestBase(server)
{
    // ------------------------------------------------------------------ id syntax

    /// <summary>
    /// § 11.1.4 and § 11.2 step 2. Every one of these is a separator, a dot segment, a rooted path
    /// or a device name once something decodes it, so none may reach the filesystem.
    /// </summary>
    public static TheoryData<string, string> HostileIdSegments() => new()
    {
        { "literal parent", ".." },
        { "encoded parent slash", "..%2Fsecret.jpg" },
        { "encoded parent backslash", "..%5Csecret.jpg" },
        { "lowercase encoded dots", "%2e%2e%2fsecret.jpg" },
        { "uppercase encoded dots", "%2E%2E%2Fsecret.jpg" },
        { "double encoded", "%252e%252e%252fsecret.jpg" },
        { "overlong utf8 slash", "..%c0%afsecret.jpg" },
        { "utf8 overlong dot", "%c0%aesecret.jpg" },
        { "absolute posix", "%2Fetc%2Fpasswd" },
        { "absolute windows drive", "C%3A%5CWindows%5Cwin.ini" },
        { "unc", "%5C%5Chost%5Cshare%5Cx.jpg" },
        { "device name con", "CON" },
        { "device name nul with extension", "NUL.jpg" },
        { "device path", "%5C%5C.%5CPhysicalDrive0" },
        { "long path prefix", "%5C%5C%3F%5CC%3A%5Cwin.ini" },
        { "trailing dot", "secret.jpg." },
        { "trailing space", "secret.jpg%20" },
        { "newline", "secret%0A.jpg" },
        { "single dot", "." },
        { "empty after decode", "%20" },
        { "invalid utf8", "%ff%fe.jpg" },
        { "bad escape", "%zz.jpg" },
        { "truncated escape", "abc%2" },
        { "255 plus one code units", "" },
    };

    [Theory]
    [MemberData(nameof(HostileIdSegments))]
    public async Task A_hostile_id_never_reaches_a_file(string label, string segment)
    {
        if (label == "255 plus one code units")
            segment = new string('a', 252) + ".jpg";   // 256 code units

        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        (await client.OpenSessionAsync(folder.Path)).ShouldHaveStatus(201, "open a session to audit /media");

        // A file the id might reach if the guard leaked, one level above the session folder.
        var outside = Path.Combine(Path.GetDirectoryName(folder.Path)!, "secret.jpg");
        await File.WriteAllBytesAsync(outside, MediaFixtures.Jpeg(64, 64));

        try
        {
            foreach (var verb in new[] { "meta", "still", "thumb", "video" })
            {
                var response = await client.GetAsync($"/api/v1/media/{segment}/{verb}");

                Assert.True(
                    response.StatusCode is 400 or 403 or 404,
                    $"SERVER_SPEC.md § 11.1/§ 11.2 ({label}): GET /media/{segment}/{verb} must be refused " +
                    $"as an illegal id (400), an escape (403) or an unknown id (404). It answered " +
                    $"{response.StatusCode} with {response.Body.Length} bytes.");

                Assert.True(
                    response.ErrorCode is "invalid_media_id" or "media_outside_session" or "unknown_media_id"
                        or "media_extension_not_allowed" or "not_found" or "media_file_missing",
                    $"({label}) GET /media/{segment}/{verb} answered error code '{response.ErrorCode}'.");
            }
        }
        finally
        {
            File.Delete(outside);
            await client.CloseSessionAsync();
        }
    }

    // ------------------------------------------------------------------ symlinks

    /// <summary>
    /// The escape the contract's own algorithm does not close.
    /// <para/>
    /// § 11.2 step 5 is "<c>Path.Combine(session.folder, id)</c>, resolve with
    /// <c>Path.GetFullPath</c>, and verify the result's directory is exactly the session folder".
    /// <c>Path.GetFullPath</c> is string canonicalisation: it collapses <c>..</c> and separators and
    /// does <b>not</b> resolve a symlink. So a link inside the session folder whose name ends in an
    /// allowed extension passes every one of the six steps and the endpoint serves the bytes of
    /// whatever it points at — with <c>Range</c> support, so the whole file comes back.
    /// <para/>
    /// The <c>video</c> endpoint is the worst case because it serves the original bytes untouched.
    /// </summary>
    [Fact]
    public async Task A_symlink_in_the_session_folder_serves_a_file_from_outside_it()
    {
        if (OperatingSystem.IsWindows()) return;   // creating a symlink needs a privilege there

        using var folder = LibraryFolder.SixStills();

        var secretDirectory = Path.Combine(Path.GetTempPath(), "rm2-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretDirectory);
        var secret = Path.Combine(secretDirectory, "private-key.pem");
        const string secretText = "-----BEGIN PRIVATE KEY-----\nAUDIT-CANARY-DO-NOT-SERVE\n-----END PRIVATE KEY-----\n";
        await File.WriteAllTextAsync(secret, secretText);

        // The link is a plain filename inside the session folder, so it satisfies § 11.1.4 exactly.
        File.CreateSymbolicLink(Path.Combine(folder.Path, "holiday.mp4"), secret);

        var client = await ClientAsync();
        try
        {
            (await client.OpenSessionAsync(folder.Path))
                .ShouldHaveStatus(201, "a folder holding a symlink still opens");

            var response = await client.VideoAsync("holiday.mp4");

            Assert.False(
                response.StatusCode == 200 && Encoding.UTF8.GetString(response.Body).Contains("AUDIT-CANARY"),
                "SERVER_SPEC.md § 11.2: /media/* must not serve bytes from outside the session folder. " +
                "A symlink named holiday.mp4 inside the folder pointed at " + secret + " and " +
                $"GET /media/holiday.mp4/video returned {response.Body.Length} bytes of it. " +
                "Path.GetFullPath in step 5 canonicalises the string and does not resolve the link, so " +
                "the parent-directory check passes. With full-filesystem browse enabled this is an " +
                "arbitrary-file read for any holder of a bearer token.");
        }
        finally
        {
            await client.CloseSessionAsync();
            try { Directory.Delete(secretDirectory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The same escape on the stills path, where the bytes are re-encoded rather than passed
    /// through: a private photograph one directory above the session folder is rendered and served,
    /// and <c>meta</c> hands over its exact size and mtime first.
    /// </summary>
    [Fact]
    public async Task A_symlink_serves_and_measures_a_still_from_outside_the_session()
    {
        if (OperatingSystem.IsWindows()) return;   // creating a symlink needs a privilege there

        using var folder = LibraryFolder.SixStills();

        var privateDirectory = Path.Combine(Path.GetTempPath(), "rm2-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateDirectory);
        var privatePhoto = Path.Combine(privateDirectory, "not-for-the-lan.jpg");
        var bytes = MediaFixtures.Jpeg(1024, 768);
        await File.WriteAllBytesAsync(privatePhoto, bytes);

        File.CreateSymbolicLink(Path.Combine(folder.Path, "vacation.jpg"), privatePhoto);

        var client = await ClientAsync();
        try
        {
            (await client.OpenSessionAsync(folder.Path)).ShouldHaveStatus(201, "open the session");

            var meta = await client.MetaAsync("vacation.jpg");
            var size = meta.StatusCode == 200 && meta.Json is { } json &&
                       json.TryGetProperty("sizeBytes", out var sizeBytes)
                ? sizeBytes.GetInt64()
                : -1;

            var still = await client.StillAsync("vacation.jpg", width: 360, format: "jpeg");

            Assert.True(size != bytes.LongLength && still.StatusCode != 200,
                "SERVER_SPEC.md § 11.2: a symlink named vacation.jpg inside the session folder pointed at " +
                $"{privatePhoto}. GET /media/vacation.jpg/meta reported sizeBytes {size} (the real length is " +
                $"{bytes.LongLength}) and GET .../still answered {still.StatusCode} with " +
                $"{still.Body.Length} bytes. Step 5's Path.GetFullPath does not resolve symlinks, so the " +
                "\"directory is exactly the session folder\" check passes for a file that is not in it.");
        }
        finally
        {
            await client.CloseSessionAsync();
            try { Directory.Delete(privateDirectory, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ non-media

    /// <summary>
    /// § 11.2 step 3: an extension in neither list is <c>403 media_extension_not_allowed</c>, and a
    /// file that was never scanned is not a record. Both must hold for a plain text file sitting in
    /// the session folder.
    /// </summary>
    [Fact]
    public async Task A_non_media_file_in_the_session_folder_is_not_reachable()
    {
        using var folder = LibraryFolder.SixStills();
        folder.WriteText("rankmaster_db.json", "{\"images\":{}}");
        folder.WriteText("notes.txt", "AUDIT-CANARY");
        folder.WriteText("id_rsa", "AUDIT-CANARY");

        var client = await ClientAsync();
        try
        {
            (await client.OpenSessionAsync(folder.Path)).ShouldHaveStatus(201, "open the session");

            foreach (var id in new[] { "notes.txt", "id_rsa", "rankmaster_db.json", ".rankmaster.lock" })
            {
                foreach (var verb in new[] { "meta", "still", "thumb", "video" })
                {
                    var response = await client.GetAsync($"/api/v1/media/{Rm2Client.EncodeId(id)}/{verb}");
                    Assert.True(response.StatusCode is 403 or 404,
                        $"SERVER_SPEC.md § 11.2: GET /media/{id}/{verb} must be 403 or 404, not " +
                        $"{response.StatusCode}");
                    Assert.DoesNotContain("AUDIT-CANARY", response.Text, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            await client.CloseSessionAsync();
        }
    }

    /// <summary>
    /// § 11.2: "Subfolders are never reachable." <c>discarded/</c> is the one an attacker knows the
    /// name of, because the contract names it.
    /// </summary>
    [Fact]
    public async Task A_file_in_a_subfolder_is_not_reachable_by_any_spelling()
    {
        using var folder = LibraryFolder.SixStills();
        folder.Subfolder("discarded");
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "discarded", "hidden.jpg"),
            MediaFixtures.Jpeg(64, 64));

        var client = await ClientAsync();
        try
        {
            (await client.OpenSessionAsync(folder.Path)).ShouldHaveStatus(201, "open the session");

            foreach (var id in new[]
            {
                "discarded/hidden.jpg",
                "discarded%2Fhidden.jpg",
                "discarded%5Chidden.jpg",
                "discarded%252Fhidden.jpg",
                ".%2Fdiscarded%2Fhidden.jpg",
            })
            {
                var response = await client.GetAsync($"/api/v1/media/{id}/meta");
                Assert.True(response.StatusCode is 400 or 403 or 404,
                    $"SERVER_SPEC.md § 11.2: /media/{id}/meta reaches a subfolder; it answered " +
                    $"{response.StatusCode} {response.Text}");
            }
        }
        finally
        {
            await client.CloseSessionAsync();
        }
    }

    /// <summary>
    /// § 11.2 step 1: there is no way to fetch bytes without a session, and closing one must take
    /// the bytes away again — including for an id that was valid a moment earlier.
    /// </summary>
    [Fact]
    public async Task Closing_the_session_revokes_every_media_url()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        (await client.OpenSessionAsync(folder.Path)).ShouldHaveStatus(201, "open the session");
        (await client.MetaAsync(MediaFixtures.PlainJpeg)).ShouldHaveStatus(200, "the id resolves while open");

        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "DELETE /session closes it");

        var after = await client.MetaAsync(MediaFixtures.PlainJpeg);
        after.ShouldHaveStatus(404, "SERVER_SPEC.md § 11.2 step 1: no session, no bytes");
        Assert.Equal("no_session", after.ErrorCode);
    }

    /// <summary>
    /// A session opened on folder A must not serve a file that lives in folder B, even when B was
    /// the session folder a moment ago and the client still holds the id.
    /// </summary>
    [Fact]
    public async Task An_id_from_a_previous_session_folder_is_not_served_by_the_next_one()
    {
        using var first = LibraryFolder.SixStills();
        using var second = LibraryFolder.TwoStills();
        second.WriteText("unique-to-second.txt", "x");

        var client = await ClientAsync();
        (await client.OpenSessionAsync(first.Path)).ShouldHaveStatus(201, "open on the first folder");
        (await client.MetaAsync(MediaFixtures.UppercaseExtension))
            .ShouldHaveStatus(200, "an id only the first folder has");
        (await client.CloseSessionAsync()).ShouldHaveStatus(204, "close");

        (await client.OpenSessionAsync(second.Path)).ShouldHaveStatus(201, "open on the second folder");
        try
        {
            var response = await client.MetaAsync(MediaFixtures.UppercaseExtension);
            response.ShouldHaveStatus(404,
                "SERVER_SPEC.md § 11.2 step 4: membership is against the OPEN session's records");
            Assert.Equal("unknown_media_id", response.ErrorCode);
        }
        finally
        {
            await client.CloseSessionAsync();
        }
    }
}
