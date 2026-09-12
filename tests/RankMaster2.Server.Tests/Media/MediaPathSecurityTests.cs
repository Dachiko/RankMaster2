using System.Net;
using System.Text.Json;
using RankMaster2.Server.Media;
using RankMaster2.Server.Tests.Fixtures;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// SERVER_SPEC.md § 11.2 and SERVER_PLAN.md § 3.6: <c>/media/*</c> "is the one place the token
/// does not buy access to the whole filesystem". Everywhere else a paired device may browse the
/// disk; here it may read the open session folder and nothing else.
/// <para/>
/// The check that matters is § 11.2 step 5, and its order is the point: combine, canonicalise, and
/// <b>then</b> verify the parent is exactly the session folder. Validating the string first and
/// resolving afterwards is the version of this that lets a separator through.
/// </summary>
public class MediaPathSecurityTests
{
    private static string Escape(string id) => "/api/v1/media/" + id + "/meta";

    private static StubSession SessionWithNeighbour(out string neighbourPath)
    {
        var session = new StubSession();
        session.Add("inside.jpg", MediaFixtures.Jpeg(64, 48));

        // A real file one level up, which a traversal would reach if the check were missing.
        neighbourPath = Path.Combine(Directory.GetParent(session.Folder)!.FullName, "outside-" + Guid.NewGuid().ToString("N")[..8] + ".jpg");
        File.WriteAllBytes(neighbourPath, MediaFixtures.Jpeg(64, 48));
        return session;
    }

    /// <summary>
    /// Traversal in every spelling a client can put in a path segment. Each one is refused before
    /// any file is opened; none returns the neighbouring file's bytes.
    /// </summary>
    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("..%5C..%5Cwindows%5Cwin.ini")]
    [InlineData("%2E%2E%2F%2E%2E%2Fetc%2Fpasswd")]
    [InlineData("%2e%2e%2fsecret.jpg")]
    [InlineData("..%252F..%252Fetc%252Fpasswd")]
    [InlineData("%2F")]
    [InlineData("%5C")]
    [InlineData("..")]
    [InlineData("%2E%2E")]
    [InlineData("sub%2Ffile.jpg")]
    public async Task TraversalAttempts_NeverLeaveTheSessionFolder(string segment)
    {
        var session = SessionWithNeighbour(out var neighbour);
        try
        {
            await using var host = new MediaHost(session);

            var response = await host.Client.GetAsync(Escape(segment));

            Assert.True(
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
                $"{segment} answered {(int)response.StatusCode}; it must never be a 200.");

            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("JFIF", body);

            // A bare ".." or "%2E%2E" collapses during URI normalisation, so the request never
            // reaches a /media route at all and answers the host's routing 404 with no body.
            // § 11.1.5 allows exactly that: "Servers hosted where the framework rejects encoded
            // separators before routing MAY return the framework's 400". Where the request does
            // reach the media layer, it carries the envelope and one of the codes below.
            if (body.Length == 0)
                return;

            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.GetProperty("error").GetProperty("code").GetString();

            Assert.Contains(code, new[] { "media_outside_session", "invalid_media_id", "unknown_media_id", "not_found" });
        }
        finally
        {
            session.Dispose();
            File.Delete(neighbour);
        }
    }

    /// <summary>
    /// § 11.2: "Subfolders are never reachable: <c>discarded/</c>, <c>special 1/</c> and
    /// <c>rankmaster_backup_*</c> hold files that are, by definition, no longer records."
    /// </summary>
    [Theory]
    [InlineData("discarded")]
    [InlineData("special 1")]
    [InlineData("rankmaster_backup_20260912")]
    public async Task SubfoldersOfTheSessionAreUnreachable(string subfolder)
    {
        using var session = new StubSession();
        session.Add("inside.jpg", MediaFixtures.Jpeg(64, 48));
        session.AddUnreachable(Path.Combine(subfolder, "hidden.jpg"), MediaFixtures.Jpeg(64, 48));

        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Escape(MediaIdCodec.Encode(subfolder + "/hidden.jpg")));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("media_outside_session", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// An absolute path in the segment. Even if it names a file that exists and is readable, it is
    /// not a record and it is not in the folder.
    /// </summary>
    [Fact]
    public async Task AbsolutePaths_AreRefused()
    {
        using var session = new StubSession();
        session.Add("inside.jpg", MediaFixtures.Jpeg(64, 48));
        await using var host = new MediaHost(session);

        foreach (var segment in new[] { "%2Fetc%2Fpasswd", "%2Fetc%2Fhostname", "C%3A%5Cwindows%5Cwin.ini" })
        {
            var response = await host.Client.GetAsync(Escape(segment));
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// § 11.2 step 3: the extension check runs even though step 4 would catch it anyway, "so the
    /// rule holds even if the record set is ever populated from somewhere else". This test forces
    /// that case: a non-media file that <i>is</i> in the record set is still refused on extension.
    /// </summary>
    [Theory]
    [InlineData("rankmaster_db.json")]
    [InlineData("notes.txt")]
    [InlineData("secrets.env")]
    [InlineData(".rankmaster.lock")]
    [InlineData("run.sh")]
    public async Task NonMediaExtensions_AreRefusedEvenWhenTheyAreRecords(string id)
    {
        using var session = new StubSession();
        session.Add(id, "sensitive"u8.ToArray());
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Escape(MediaIdCodec.Encode(id)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("media_extension_not_allowed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// The library database sits in the session folder next to the photos, so it is the single
    /// most likely thing for a traversal-free bug to expose. It never is.
    /// </summary>
    [Fact]
    public async Task TheLibraryDatabaseIsNeverServed()
    {
        using var session = new StubSession();
        session.Add("inside.jpg", MediaFixtures.Jpeg(64, 48));
        await File.WriteAllTextAsync(Path.Combine(session.Folder, "rankmaster_db.json"), "{\"images\":{}}");

        await using var host = new MediaHost(session);

        foreach (var verb in new[] { "meta", "still", "thumb", "video" })
        {
            var response = await host.Client.GetAsync($"/api/v1/media/rankmaster_db.json/{verb}");
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("images", await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// § 11.1.4 requires the id to equal its own <c>Path.GetFileName</c>. The resolver repeats that
    /// after canonicalisation, so a record carrying a separator — however it got into the record
    /// set — is a 403 rather than a read outside the folder.
    /// </summary>
    [Fact]
    public void Resolver_RefusesARecordThatCarriesASeparator()
    {
        using var session = new StubSession();
        session.Track("../outside.jpg");
        session.Track("sub/inside.jpg");

        var resolver = new MediaResolver(session);

        Assert.Equal(MediaResolution.OutsideSession, resolver.Resolve(MediaIdCodec.Encode("../outside.jpg")).Resolution);
        Assert.Equal(MediaResolution.OutsideSession, resolver.Resolve(MediaIdCodec.Encode("sub/inside.jpg")).Resolution);
    }

    /// <summary>
    /// § 11.1.6: "The server MUST return the on-disk spelling ... not the spelling the client
    /// sent." On a case-insensitive filesystem the two differ, and an ETag keyed on the caller's
    /// casing would not be the same entity tag for the same bytes.
    /// </summary>
    [Fact]
    public void ResolvedIdIsTheOnDiskSpelling()
    {
        using var session = new StubSession();
        session.Add("Sierra.jpg", MediaFixtures.Jpeg(32, 24));

        var result = new MediaResolver(session).Resolve("Sierra.jpg");

        Assert.True(result.Ok);
        Assert.Equal("Sierra.jpg", result.Media!.Id);
        Assert.Equal("Sierra.jpg", Path.GetFileName(result.Media.Path));
    }

    /// <summary>
    /// Step 1 comes before every other step: with no session open there is nothing to leak, and
    /// nothing about the filesystem is revealed by the answer either way.
    /// </summary>
    [Fact]
    public async Task WithNoSession_EvenATraversalIsJustNoSession()
    {
        await using var host = new MediaHost(new NoSession());

        var response = await host.Client.GetAsync(Escape("..%2F..%2Fetc%2Fpasswd"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("no_session", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
