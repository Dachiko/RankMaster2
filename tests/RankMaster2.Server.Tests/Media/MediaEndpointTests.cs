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

    [Fact]
    public async Task Meta_DescribesAStill_WithOrientedDimensions()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "meta"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(Still, root.GetProperty("id").GetString());
        Assert.Equal("still", root.GetProperty("kind").GetString());
        Assert.Equal(1600, root.GetProperty("width").GetInt32());
        Assert.Equal(1200, root.GetProperty("height").GetInt32());
        Assert.True(root.GetProperty("rankable").GetBoolean());
        Assert.Matches("^[0-9a-f]{16}$", root.GetProperty("mediaVersion").GetString()!);
        Assert.EndsWith("Z", root.GetProperty("modifiedAt").GetString()!);

        var rating = root.GetProperty("rating");
        Assert.Equal(
            rating.GetProperty("mu").GetDouble() - 3 * rating.GetProperty("sigma").GetDouble(),
            rating.GetProperty("conservative").GetDouble(),
            6);
    }

    /// <summary>
    /// § 12.1: width and height are "<c>null</c> for a video, always", and <c>MediaMeta</c> carries
    /// no duration, codec, frame rate or bitrate — every one of those would need a decoded frame.
    /// </summary>
    [Fact]
    public async Task Meta_OnAVideo_HasNullDimensionsAndNothingThatNeedsADecoder()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync(Url(Video, "meta")));
        var root = doc.RootElement;

        Assert.Equal("video", root.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("height").ValueKind);

        foreach (var forbidden in new[] { "durationMs", "codec", "frameRate", "bitrate" })
            Assert.False(root.TryGetProperty(forbidden, out _), $"MediaMeta must not carry {forbidden}.");

        // A video in a stills folder is in the records and served, but never rankable (§ 16.8).
        Assert.False(root.GetProperty("rankable").GetBoolean());
    }

    [Fact]
    public async Task Meta_CarriesEveryKeyTheContractRequires()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync(Url(Still, "meta")));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string>(Harness.ContractShape.MediaMetaKeys),
            keys);
    }

    // ----------------------------------------------------------------- still

    [Fact]
    public async Task Still_DefaultsTo1080Jpeg()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "still"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("\"s1080j-", response.Headers.ETag!.ToString());
        Assert.False(response.Headers.ETag.IsWeak);

        using var image = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1080, image.Width);
    }

    [Fact]
    public async Task Still_CarriesTheImmutablePrivateCacheHeaders()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "still"));
        var cacheControl = response.Headers.CacheControl!;

        Assert.True(cacheControl.Private);
        Assert.Equal(TimeSpan.FromSeconds(31536000), cacheControl.MaxAge);
        Assert.Contains("immutable", cacheControl.ToString());
        Assert.False(cacheControl.Public);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(540)]
    [InlineData(720)]
    [InlineData(1080)]
    public async Task Still_ServesEveryAllowedWidth(int width)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "still", $"?w={width}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith($"\"s{width}j-", response.Headers.ETag!.ToString());

        using var image = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(width, image.Width);
    }

    /// <summary>
    /// § 12.3: "Any other value, including a smaller one that 'would be fine' → 400
    /// <c>unsupported_width</c>". Capping the set is what bounds the disk cache, so a clamp here
    /// would quietly unbound it.
    /// </summary>
    [Theory]
    [InlineData("1000")]
    [InlineData("100")]
    [InlineData("4096")]
    [InlineData("0")]
    [InlineData("-1080")]
    [InlineData("1080.0")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Still_RejectsAWidthOutsideTheSet_RatherThanClamping(string w)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "still", $"?w={w}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await ErrorOf(response);
        Assert.Equal("unsupported_width", error.GetProperty("code").GetString());

        var allowed = error.GetProperty("details").GetProperty("allowed")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 360, 540, 720, 1080, 1440, 2160 }, allowed);
    }

    /// <summary>§ 12.3: "<c>thumb</c> ignores <c>w</c> entirely ... MUST be ignored, not rejected."</summary>
    [Theory]
    [InlineData("999")]
    [InlineData("1080")]
    [InlineData("nonsense")]
    public async Task Thumb_IgnoresWidth_RatherThanRejectingIt(string w)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "thumb", $"?w={w}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("\"t320j-", response.Headers.ETag!.ToString());

        using var image = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(320, image.Width);
    }

    /// <summary>
    /// § 12.3: "Never upscale. If the source's long edge after orientation is below the requested
    /// width, the server serves it at source size. The response is still keyed by the requested
    /// <c>w</c>."
    /// </summary>
    [Fact]
    public async Task Still_NeverUpscales_ButIsStillKeyedByTheRequestedWidth()
    {
        using var session = new StubSession();
        session.Add("small.jpg", MediaFixtures.Jpeg(200, 150));
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url("small.jpg", "still", "?w=2160"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("\"s2160j-", response.Headers.ETag!.ToString());

        using var image = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(200, image.Width);
        Assert.Equal(150, image.Height);
    }

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

    /// <summary>
    /// § 12.1 and § 12.3 applied to EXIF: the dimensions <c>meta</c> reports and the pixels
    /// <c>still</c> delivers are both after orientation, so a rotated photo is not served on its side.
    /// </summary>
    [Fact]
    public async Task Still_AppliesExifOrientation()
    {
        using var session = new StubSession();
        session.Add("rotated.jpg", MediaFixtures.ExifRotatedJpeg());
        await using var host = new MediaHost(session);

        using var meta = JsonDocument.Parse(await host.Client.GetStringAsync(Url("rotated.jpg", "meta")));
        Assert.Equal(MediaFixtures.ExifRotatedDisplayed.Width, meta.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(MediaFixtures.ExifRotatedDisplayed.Height, meta.RootElement.GetProperty("height").GetInt32());

        using var image = SKBitmap.Decode(await host.Client.GetByteArrayAsync(Url("rotated.jpg", "still")));
        Assert.True(image.Height > image.Width, "The stored image is landscape; oriented it is portrait.");
    }

    // ---------------------------------------------------------------- format

    [Fact]
    public async Task Format_QueryWins_AndDoesNotVaryOnAccept()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still", "?format=jpeg"));
        request.Headers.Accept.ParseAdd("image/webp");

        var response = await host.Client.SendAsync(request);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("\"s1080j-", response.Headers.ETag!.ToString());
        Assert.DoesNotContain("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task Accept_SelectsWebp_AndEarnsVaryAccept()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still"));
        request.Headers.Accept.ParseAdd("image/webp,image/jpeg;q=0.8");

        var response = await host.Client.SendAsync(request);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("\"s1080w-", response.Headers.ETag!.ToString());
        Assert.Contains("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task NoAcceptPreference_IsJpeg_AndDoesNotVary()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still"));
        request.Headers.Accept.ParseAdd("*/*");

        var response = await host.Client.SendAsync(request);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Accept", response.Headers.Vary);
    }

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

    [Theory]
    [InlineData("png")]
    [InlineData("JPEG")]
    [InlineData("avif")]
    [InlineData("")]
    public async Task UnknownFormat_IsABadRequest(string format)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "still", $"?format={format}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_format", (await ErrorOf(response)).GetProperty("code").GetString());
    }

    // ----------------------------------------------------------------- ETags

    [Fact]
    public async Task SameRequestTwice_GivesTheSameETagAndTheSameBytes()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var first = await host.Client.GetAsync(Url(Still, "still", "?w=720"));
        var second = await host.Client.GetAsync(Url(Still, "still", "?w=720"));

        Assert.Equal(first.Headers.ETag!.ToString(), second.Headers.ETag!.ToString());
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task IfNoneMatch_OnAMatch_IsA304WithNoBody()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var first = await host.Client.GetAsync(Url(Still, "still"));
        var etag = first.Headers.ETag!.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Still, "still"));
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.ToString());
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

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

    /// <summary>
    /// § 12.3: <c>v</c> is a cache buster the server "MUST accept ... MUST ignore ... and MUST NOT
    /// fail on a mismatch with the current <c>mediaVersion</c>".
    /// </summary>
    [Theory]
    [InlineData("?v=deadbeefdeadbeef")]
    [InlineData("?v=not-a-version")]
    [InlineData("?v=")]
    public async Task VersionQuery_IsAcceptedAndIgnored(string query)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var plain = await host.Client.GetAsync(Url(Still, "still"));
        var busted = await host.Client.GetAsync(Url(Still, "still", query));

        Assert.Equal(HttpStatusCode.OK, busted.StatusCode);
        Assert.Equal(plain.Headers.ETag!.ToString(), busted.Headers.ETag!.ToString());
    }

    // ----------------------------------------------------------------- video

    [Fact]
    public async Task Video_ServesTheOriginalBytes_Unmodified()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Video, "video"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/x-msvideo", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal("\"orig-", response.Headers.ETag!.ToString()[..6]);

        var served = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(session.Folder, Video)), served);
    }

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

    [Fact]
    public async Task Video_ClosedRange_Is206WithContentRange()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var whole = await File.ReadAllBytesAsync(Path.Combine(session.Folder, Video));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.Range = new RangeHeaderValue(10, 109);

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 10-109/{whole.Length}", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Equal(whole[10..110], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Video_OpenEndedRange_RunsToTheEnd()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var whole = await File.ReadAllBytesAsync(Path.Combine(session.Folder, Video));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=500-");

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 500-{whole.Length - 1}/{whole.Length}", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(whole[500..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Video_SuffixRange_TakesTheTail()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var whole = await File.ReadAllBytesAsync(Path.Combine(session.Folder, Video));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=-500");

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {whole.Length - 500}-{whole.Length - 1}/{whole.Length}", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(whole[^500..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Video_UnsatisfiableRange_Is416WithAStarContentRangeAndTheEnvelope()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var length = new FileInfo(Path.Combine(session.Folder, Video)).Length;

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", $"bytes={length + 1000}-");

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal($"bytes */{length}", response.Content.Headers.ContentRange!.ToString());

        var error = await ErrorOf(response);
        Assert.Equal("range_not_satisfiable", error.GetProperty("code").GetString());
        Assert.Equal(length, error.GetProperty("details").GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Video_MultipleRanges_ServeTheFirstOnly_NeverMultipart()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var whole = await File.ReadAllBytesAsync(Path.Combine(session.Folder, Video));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-99,200-299");

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 0-99/{whole.Length}", response.Content.Headers.ContentRange!.ToString());
        Assert.DoesNotContain("multipart", response.Content.Headers.ContentType!.MediaType!);
        Assert.Equal(whole[..100], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=200-100")]
    [InlineData("chapters=1-2")]
    [InlineData("bytes=")]
    public async Task Video_InvalidRangeSyntax_IsIgnored_And200Follows(string range)
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var whole = await File.ReadAllBytesAsync(Path.Combine(session.Folder, Video));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", range);

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(whole.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
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

    [Fact]
    public async Task Video_IfRangeMatching_ServesTheRange()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var etag = (await host.Client.GetAsync(Url(Video, "video"))).Headers.ETag!.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        request.Headers.TryAddWithoutValidation("If-Range", etag);

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(10, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Video_IfRangeNotMatching_ServesTheWholeBody()
    {
        using var session = Session();
        await using var host = new MediaHost(session);
        var length = new FileInfo(Path.Combine(session.Folder, Video)).Length;

        using var request = new HttpRequestMessage(HttpMethod.Get, Url(Video, "video"));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        request.Headers.TryAddWithoutValidation("If-Range", "\"orig-" + new string('0', 32) + "\"");

        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(length, response.Content.Headers.ContentLength);
    }

    // ------------------------------------------------------------ wrong kind

    [Theory]
    [InlineData("still")]
    [InlineData("thumb")]
    public async Task StillEndpoints_OnAVideo_AreAConflict_NotAPosterFrame(string verb)
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Video, verb));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = await ErrorOf(response);
        Assert.Equal("wrong_media_kind", error.GetProperty("code").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(Video, details.GetProperty("id").GetString());
        Assert.Equal("video", details.GetProperty("kind").GetString());
        Assert.Equal(verb, details.GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task VideoEndpoint_OnAStill_IsAConflict()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url(Still, "video"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wrong_media_kind", (await ErrorOf(response)).GetProperty("code").GetString());
    }

    // --------------------------------------------------------------- failure

    [Fact]
    public async Task NoSession_IsA404_OnEveryEndpoint()
    {
        await using var host = new MediaHost(new NoSession());

        foreach (var verb in new[] { "meta", "still", "thumb", "video" })
        {
            var response = await host.Client.GetAsync(Url(Still, verb));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no_session", (await ErrorOf(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task UnknownId_IsA404()
    {
        using var session = Session();
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url("never-scanned.jpg", "meta"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await ErrorOf(response);
        Assert.Equal("unknown_media_id", error.GetProperty("code").GetString());
        Assert.Equal("never-scanned.jpg", error.GetProperty("details").GetProperty("id").GetString());
    }

    /// <summary>
    /// § 11.3: a record whose file has gone is a 404 <c>media_file_missing</c>, and — the part
    /// that is easy to get wrong — the record is <b>not</b> dropped. "The server MUST NOT mutate
    /// session state from a <c>GET</c>."
    /// </summary>
    [Fact]
    public async Task MissingFile_IsA404_AndDoesNotDropTheRecord()
    {
        using var session = new StubSession();
        session.Track("vanished.jpg");
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url("vanished.jpg", "meta"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media_file_missing", (await ErrorOf(response)).GetProperty("code").GetString());

        Assert.True(session.TryFindRecord("vanished.jpg", out _), "A failed GET must not drop the record.");

        // And again, identically: the first call changed nothing.
        var second = await host.Client.GetAsync(Url("vanished.jpg", "meta"));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("media_file_missing", (await ErrorOf(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UndecodableFile_IsA422()
    {
        using var session = new StubSession();
        session.Add("corrupt.jpg", MediaFixtures.CorruptJpeg());
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url("corrupt.jpg", "still"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = await ErrorOf(response);
        Assert.Equal("media_decode_failed", error.GetProperty("code").GetString());
        Assert.Equal("corrupt.jpg", error.GetProperty("details").GetProperty("id").GetString());
    }

    [Fact]
    public async Task NonMediaExtension_IsA403()
    {
        using var session = new StubSession();
        session.Add("notes.txt", "not media"u8.ToArray());
        await using var host = new MediaHost(session);

        var response = await host.Client.GetAsync(Url("notes.txt", "meta"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await ErrorOf(response);
        Assert.Equal("media_extension_not_allowed", error.GetProperty("code").GetString());
        Assert.Equal("txt", error.GetProperty("details").GetProperty("extension").GetString());
    }

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
