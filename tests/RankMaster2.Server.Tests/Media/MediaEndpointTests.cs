using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RankMaster2.Server.Media;
using RankMaster2.Server.Tests.Fixtures;
using SkiaSharp;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// The four endpoints of SERVER_SPEC.md § 12.1, end to end over HTTP.
/// </summary>
public class MediaEndpointTests
{
    private const string Still = "alpha.jpg";
    private const string Awkward = "beach day #2.jpg";
    private const string Video = "clip.avi";

    private static StubSession Session()
    {
        var session = new StubSession();
        session.Add(Still, MediaFixtures.Jpeg(1600, 1200));
        session.Add(Awkward, MediaFixtures.Jpeg(400, 300));
        session.Add(Video, MediaFixtures.SmallVideo());
        return session;
    }

    private static string Url(string id, string verb, string query = "") =>
        $"/api/v1/media/{MediaIdCodec.Encode(id)}/{verb}{query}";

    // ------------------------------------------------------------------ meta




    // ----------------------------------------------------------------- still







    [Fact]
    public async Task Still_PreservesAspectRatio_AndNeverCrops()
    {
        using var session = new StubSession();
        session.Add("tall.jpg", MediaFixtures.Jpeg(600, 1800));
        await using var host = new MediaHost(session);

        using var image = SKBitmap.Decode(await host.Client.GetByteArrayAsync(Url("tall.jpg", "still", "?w=720")));

        // Long edge bounded by w, aspect ratio kept to the pixel.
        Assert.Equal(720, image.Height);
        Assert.Equal(240, image.Width);
    }


    // ---------------------------------------------------------------- format




    [Fact]
    public async Task WebpWithZeroQuality_IsNotAPreference()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still"));
        request.Headers.Accept.ParseAdd("image/webp;q=0");

        var response = await host.Client.SendAsync(request);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
    }


    // ----------------------------------------------------------------- ETags



    [Fact]
    public async Task IfNoneMatch_Star_MatchesAnyRepresentation()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still"));
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");

        Assert.Equal(HttpStatusCode.NotModified, (await host.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_WithAStaleTag_ServesTheBody()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still"));
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"s1080j-" + new string('0', 32) + "\"");

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// § 12.2: the fingerprint is over (id, size, mtime), so replacing the file changes the ETag —
    /// and the disk cache lands on a new key rather than serving the previous bytes.
    /// </summary>
    [Fact]
    public async Task ReplacingTheFile_ChangesTheETagAndTheBytes()
    {
        using var session = new StubSession();
        var path = session.Add("swap.jpg", MediaFixtures.Jpeg(800, 600));
        await using var host = new MediaHost(session);

        var before = await host.Client.GetAsync(Url("swap.jpg", "still", "?w=360"));
        var beforeBytes = await before.Content.ReadAsByteArrayAsync();

        await File.WriteAllBytesAsync(path, MediaFixtures.Jpeg(1200, 400));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var after = await host.Client.GetAsync(Url("swap.jpg", "still", "?w=360"));

        Assert.NotEqual(before.Headers.ETag!.ToString(), after.Headers.ETag!.ToString());
        Assert.NotEqual(beforeBytes, await after.Content.ReadAsByteArrayAsync());
    }


    // ----------------------------------------------------------------- video


    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("clip.mkv", "video/x-matroska")]
    [InlineData("clip.avi", "video/x-msvideo")]
    [InlineData("clip.mov", "video/quicktime")]
    public async Task Video_ContentTypeIsByExtension(string id, string expected)
    {
        using var session = new StubSession();
        session.Add(id, MediaFixtures.SmallVideo());
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(id, "video"));
        Assert.Equal(expected, response.Content.Headers.ContentType!.MediaType);
    }







    /// <summary>A zero-length file: 200 for the whole of nothing, 416 for any range of it.</summary>
    [Fact]
    public async Task Video_ZeroLengthFile_IsServedAndCannotBeRanged()
    {
        using var session = new StubSession();
        session.Add("empty.mp4", []);
        await using var host = new MediaHost(session);

        var whole = await host.Client.GetAsync(Url("empty.mp4", "video"));
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(0, whole.Content.Headers.ContentLength);
        Assert.Empty(await whole.Content.ReadAsByteArrayAsync());

        using var request = new HttpRequestMessage(HttpMethod.Get, Url("empty.mp4", "video"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-");

        var ranged = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, ranged.StatusCode);
        Assert.Equal("bytes */0", ranged.Content.Headers.ContentRange!.ToString());
    }



    // ------------------------------------------------------------ wrong kind



    // --------------------------------------------------------------- failure






    // --------------------------------------------------------------- HEAD

    [Theory]
    [InlineData("meta")]
    [InlineData("still")]
    [InlineData("thumb")]
    public async Task Head_MatchesGet_WithNoBody(string verb)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var get = await host.Client.GetAsync(Url(Still, verb));
        var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url(Still, verb)));

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentType?.ToString(), head.Content.Headers.ContentType?.ToString());
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(get.Headers.ETag?.ToString(), head.Headers.ETag?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    /// <summary>§ 12.4 calls HEAD "how a player should discover the length".</summary>
    [Fact]
    public async Task Head_OnVideo_ReportsTheLengthWithoutSendingIt()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var length = new FileInfo(Path.Combine(session.Folder, Video)).Length;

        var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url(Video, "video")));

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(length, head.Content.Headers.ContentLength);
        Assert.Equal("bytes", head.Headers.AcceptRanges.Single());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    // ------------------------------------------------------------- envelope

    [Fact]
    public async Task EveryResponse_CarriesRequestIdAndNosniff()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        foreach (var url in new[] { Url(Still, "meta"), Url(Still, "still"), Url("gone.jpg", "meta") })
        {
            var response = await host.Client.GetAsync(url);
            Assert.NotEmpty(response.Headers.GetValues("X-Request-Id").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }

    [Fact]
    public async Task ErrorEnvelope_HasTheRequiredFields_AndMatchesTheHeader()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url("gone.jpg", "meta"));
        var error = await ErrorOf(response);

        Assert.True(error.TryGetProperty("code", out _));
        Assert.True(error.TryGetProperty("message", out _));
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), error.GetProperty("requestId").GetString());
        Assert.DoesNotContain(session.Folder, error.GetProperty("message").GetString()!);
    }

    // ---------------------------------------------------------------- helper

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").Clone();
    }
}
