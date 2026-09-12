using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests;

/// <summary>
/// Media identity and bytes (SERVER_SPEC.md § 11 and § 12).
///
/// This is where the generated fixtures pay for themselves. `/media/*` is the one place a bearer
/// token does not buy the whole filesystem (§ 11.2), the id is a filename with all the sharp edges
/// that implies (§ 11.1), and the caching promises of § 12.5 are only meaningful against files whose
/// awkward properties — an EXIF rotation, an ICC profile, zero bytes, a truncation — are known
/// exactly because they were generated rather than found.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class MediaTests(Rm2Server server, ITestOutputHelper output) : SessionTestBase(server)
{
    public static readonly int[] AllowedWidths = { 360, 540, 720, 1080, 1440, 2160 };

    private async Task<(LibraryFolder Folder, Rm2Client Client)> OpenMenagerieAsync()
    {
        var folder = LibraryFolder.Menagerie();
        var client = await ClientAsync();
        (await client.OpenSessionAsync(folder.Path)).ShouldBeSnapshot(201, "open the menagerie folder");
        return (folder, client);
    }

    // ---- meta -------------------------------------------------------------------------------

    [Fact]
    public async Task Meta_describes_a_still_in_the_contract_shape()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.MetaAsync(MediaFixtures.PlainJpeg);
        response.ShouldHaveStatus(200, "GET /media/{id}/meta for a still (SERVER_SPEC.md § 12.1)");

        var meta = response.JsonBody;
        ContractShape.RequireExactKeys(meta, "MediaMeta", ContractShape.MediaMetaKeys,
                                       "GET /media/{id}/meta (SERVER_SPEC.md § 12.1)", response);
        ContractShape.RequireExactKeys(meta.GetProperty("rating"), "Rating", ContractShape.RatingKeys,
                                       "GET /media/{id}/meta", response);

        Assert.Equal(MediaFixtures.PlainJpeg, meta.GetProperty("id").GetString());
        Assert.Equal("still", meta.GetProperty("kind").GetString());
        Assert.Equal(new FileInfo(folder.File(MediaFixtures.PlainJpeg)).Length, meta.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(320, meta.GetProperty("width").GetInt32());
        Assert.Equal(240, meta.GetProperty("height").GetInt32());
        Assert.True(meta.GetProperty("rankable").GetBoolean());

        // § 12.1: conservative is μ − 3σ, computed on read and never stored.
        var rating = meta.GetProperty("rating");
        var mu = rating.GetProperty("mu").GetDouble();
        var sigma = rating.GetProperty("sigma").GetDouble();
        Assert.True(Math.Abs(rating.GetProperty("conservative").GetDouble() - (mu - 3 * sigma)) < 1e-6,
            "SERVER_SPEC.md § 12.1: `conservative` is μ − 3σ.");
    }

    /// <summary>
    /// The rotated fixture is stored 200×100 with EXIF orientation 6. § 12.1 says width and height
    /// are reported "after EXIF orientation is applied", so the answer must be 100×200 — the
    /// transposed, displayed size. A server that reports the stored size will lay out every rotated
    /// photo on every client sideways.
    /// </summary>
    [Fact]
    public async Task Meta_reports_a_rotated_still_at_its_displayed_size()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.MetaAsync(MediaFixtures.ExifRotated);
        response.ShouldHaveStatus(200, $"GET /media/{MediaFixtures.ExifRotated}/meta");

        var meta = response.JsonBody;
        var width = meta.GetProperty("width").GetInt32();
        var height = meta.GetProperty("height").GetInt32();
        output.WriteLine($"{MediaFixtures.ExifRotated}: stored {MediaFixtures.ExifRotatedStored}, " +
                         $"reported {width}×{height}");

        Assert.True((width, height) == MediaFixtures.ExifRotatedDisplayed,
            $"SERVER_SPEC.md § 12.1: width/height are reported after EXIF orientation is applied. " +
            $"The file is stored {MediaFixtures.ExifRotatedStored.Width}×{MediaFixtures.ExifRotatedStored.Height} " +
            $"with orientation 6 (rotate 90° clockwise), so it displays " +
            $"{MediaFixtures.ExifRotatedDisplayed.Width}×{MediaFixtures.ExifRotatedDisplayed.Height}. " +
            $"Got {width}×{height}.");
    }

    [Fact]
    public async Task Meta_for_a_video_has_no_dimensions_and_no_probe_fields()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.MetaAsync(MediaFixtures.Video);
        response.ShouldHaveStatus(200, $"GET /media/{MediaFixtures.Video}/meta");

        var meta = response.JsonBody;
        ContractShape.RequireExactKeys(meta, "MediaMeta", ContractShape.MediaMetaKeys,
                                       "GET /media/{id}/meta for a video", response);

        Assert.Equal("video", meta.GetProperty("kind").GetString());

        // § 12.1: width/height are "null for a video, always" — producing them would mean decoding
        // a frame, which SERVER_PLAN.md § 7 forbids outright.
        Assert.True(meta.GetProperty("width").ValueKind == System.Text.Json.JsonValueKind.Null,
            "SERVER_SPEC.md § 12.1: width is always null for a video — the server never decodes a frame.");
        Assert.True(meta.GetProperty("height").ValueKind == System.Text.Json.JsonValueKind.Null,
            "SERVER_SPEC.md § 12.1: height is always null for a video.");

        // The key-set check above already rejects durationMs, codec, frameRate and bitrate.
        Assert.False(meta.GetProperty("rankable").GetBoolean(),
            "SPEC.md § Media policy: in a mixed folder the policy is 'still', so a video is not rankable.");
    }

    // ---- stills: widths, formats, upscaling -------------------------------------------------

    [Theory]
    [InlineData(360)]
    [InlineData(540)]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public async Task Every_allowed_width_is_served(int width)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.Large, width);
        response.ShouldHaveStatus(200, $"SERVER_SPEC.md § 12.3: w={width} is in the allowed set");
        Assert.True(response.Body.Length > 0, $"w={width} returned an empty body.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(320)]     // the thumb width, which is still not a legal `w` for /still
    [InlineData(800)]
    [InlineData(1081)]
    [InlineData(4096)]
    [InlineData(100000)]
    public async Task A_width_outside_the_allowed_set_is_refused(int width)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.PlainJpeg, width);
        var failure = response.ShouldBeError("unsupported_width",
            $"SERVER_SPEC.md § 12.3: w={width} is not in the allowed set. 'Any other value, including a " +
            "smaller one that would be fine' — capping the set is what keeps the disk cache bounded.");

        Assert.Equal(width, failure.Detail("requested", "unsupported_width details").GetInt32());

        var allowed = failure.Detail("allowed", "unsupported_width details")
            .EnumerateArray().Select(w => w.GetInt32()).ToArray();
        Assert.Equal(AllowedWidths, allowed);
    }

    [Fact]
    public async Task A_width_that_is_not_a_number_is_refused()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.GetAsync($"/media/{Rm2Client.EncodeId(MediaFixtures.PlainJpeg)}/still?w=wide");
        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 12.3: a non-numeric w is not in the allowed set",
            "unsupported_width", "invalid_request");
    }

    [Fact]
    public async Task An_omitted_width_means_ten_eighty()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var implicitWidth = await client.StillAsync(MediaFixtures.Large);
        implicitWidth.ShouldHaveStatus(200, "SERVER_SPEC.md § 12.3: `w` omitted means 1080");

        var explicitWidth = await client.StillAsync(MediaFixtures.Large, 1080);
        explicitWidth.ShouldHaveStatus(200, "w=1080");

        // § 12.2: the ETag is keyed by the requested width, so these two must agree exactly.
        Assert.Equal(explicitWidth.HeaderOrNull("ETag"), implicitWidth.HeaderOrNull("ETag"));
        Assert.Equal(explicitWidth.Body.Length, implicitWidth.Body.Length);
    }

    /// <summary>
    /// § 12.3: "Never upscale. If the source's long edge after orientation is below the requested
    /// width, the server serves it at source size. The response is still keyed by the requested w."
    /// The 320×240 fixture is smaller than every allowed width, so every width must return the same
    /// pixels — and a different ETag each time.
    /// </summary>
    [Fact]
    public async Task A_small_source_is_never_upscaled_but_is_still_keyed_by_the_requested_width()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var tags = new Dictionary<int, string>();
        var sizes = new Dictionary<int, int>();

        foreach (var width in AllowedWidths)
        {
            var response = await client.StillAsync(MediaFixtures.PlainJpeg, width);
            response.ShouldHaveStatus(200, $"still at w={width}");
            tags[width] = response.Header("ETag", $"still at w={width} (SERVER_SPEC.md § 12.2)");
            sizes[width] = response.Body.Length;
            output.WriteLine($"w={width,5}: {response.Body.Length,8} bytes, ETag {tags[width]}");
        }

        Assert.True(sizes.Values.Distinct().Count() == 1,
            "SERVER_SPEC.md § 12.3: the server never upscales, so a 320×240 source is served at source size " +
            "for every requested width. The body lengths differ: " +
            string.Join(", ", sizes.Select(e => $"w={e.Key} → {e.Value} B")));

        Assert.True(tags.Values.Distinct().Count() == AllowedWidths.Length,
            "SERVER_SPEC.md § 12.2: `{w}` in the variant is the requested width, not the delivered one, " +
            "'so two clients asking for different widths of a small image get different ETags even when the " +
            "bytes match'. Got " + tags.Values.Distinct().Count() + " distinct ETags for " +
            AllowedWidths.Length + " widths.");
    }

    [Fact]
    public async Task The_thumbnail_is_fixed_at_three_hundred_and_twenty_and_ignores_a_width()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var plain = await client.ThumbAsync(MediaFixtures.Large);
        plain.ShouldHaveStatus(200, "SERVER_SPEC.md § 12.1: the thumbnail endpoint");

        // § 12.3: "A `w` on `thumb` MUST be ignored, not rejected." Even an illegal one.
        foreach (var width in new[] { 1080, 320, 99999, 0 })
        {
            var withWidth = await client.ThumbAsync(MediaFixtures.Large, width: width);
            withWidth.ShouldHaveStatus(200,
                $"SERVER_SPEC.md § 12.3: thumb ignores `w` entirely — w={width} must be ignored, not rejected");

            Assert.Equal(plain.HeaderOrNull("ETag"), withWidth.HeaderOrNull("ETag"));
            Assert.Equal(plain.Body.Length, withWidth.Body.Length);
        }

        var tag = plain.Header("ETag", "thumb ETag");
        Assert.True(tag.Contains("t320", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 12.2: a thumbnail's ETag variant is t320j or t320w. Got {tag}.");
    }

    [Theory]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("webp", "image/webp")]
    public async Task An_explicit_format_wins_and_does_not_vary_on_accept(string format, string contentType)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // Accept says the opposite of `format`; § 12.3 puts `format=` first in strict precedence.
        var contrary = format == "jpeg" ? "image/webp,image/*;q=0.8" : "image/jpeg";
        var response = await client.StillAsync(MediaFixtures.ThirdJpeg, format: format, accept: contrary);

        response.ShouldHaveStatus(200, $"SERVER_SPEC.md § 12.3: format={format} wins over Accept");
        Assert.True(response.ContentType?.StartsWith(contentType, StringComparison.OrdinalIgnoreCase) == true,
            $"SERVER_SPEC.md § 12.3: format={format} must produce {contentType}, got '{response.ContentType}'.");

        // § 12.3: "A response whose format was chosen by Accept MUST carry Vary: Accept. One chosen
        // by format= MUST NOT."
        var vary = response.HeaderOrNull("Vary");
        Assert.True(vary is null || !vary.Contains("Accept", StringComparison.OrdinalIgnoreCase),
            $"SERVER_SPEC.md § 12.3: a format chosen by `format=` must not carry Vary: Accept. Got '{vary}'.");
    }

    [Fact]
    public async Task Webp_in_accept_selects_webp_and_says_so_in_vary()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.ThirdJpeg, accept: "image/webp,image/jpeg;q=0.9");
        response.ShouldHaveStatus(200, "SERVER_SPEC.md § 12.3: Accept including image/webp selects WebP");

        Assert.True(response.ContentType?.StartsWith("image/webp", StringComparison.OrdinalIgnoreCase) == true,
            $"SERVER_SPEC.md § 12.3 step 2: `image/webp` in Accept with a non-zero q selects WebP. " +
            $"Got '{response.ContentType}'.");

        var vary = response.HeaderOrNull("Vary");
        Assert.True(vary is not null && vary.Contains("Accept", StringComparison.OrdinalIgnoreCase),
            "SERVER_SPEC.md § 12.3: a response whose format was chosen by Accept MUST carry Vary: Accept. " +
            $"Without it a shared cache would serve WebP to a client that cannot read it. Got '{vary ?? "(absent)"}'.");
    }

    [Fact]
    public async Task No_accept_and_no_format_means_jpeg()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.ThirdJpeg, accept: "image/*");
        response.ShouldHaveStatus(200, "SERVER_SPEC.md § 12.3 step 3: otherwise JPEG");

        Assert.True(response.ContentType?.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) == true,
            $"SERVER_SPEC.md § 12.3: with no `format` and no `image/webp` in Accept, the answer is JPEG. " +
            $"Got '{response.ContentType}'.");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("avif")]
    [InlineData("JPEG")]
    [InlineData("")]
    public async Task A_format_that_is_neither_jpeg_nor_webp_is_refused(string format)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.PlainJpeg, format: format);
        var failure = response.ShouldBeError("unsupported_format",
            $"SERVER_SPEC.md § 12.3: format must be exactly 'jpeg' or 'webp'; '{format}' is not");

        var allowed = failure.Detail("allowed", "unsupported_format details")
            .EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Equal(new[] { "jpeg", "webp" }, allowed);
    }

    // ---- caching ----------------------------------------------------------------------------

    [Fact]
    public async Task Byte_responses_are_privately_cacheable_forever_by_etag()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.ThirdJpeg, 720);
        response.ShouldHaveStatus(200, "a still");

        var cacheControl = response.Header("Cache-Control", "a still (SERVER_SPEC.md § 12.5)");

        // § 12.5: "`private` is mandatory. These bytes are the user's photos; no shared cache may hold them."
        Assert.True(cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase),
            $"SERVER_SPEC.md § 12.5: media bytes are the user's photos and MUST be `private`. Got '{cacheControl}'.");
        Assert.True(cacheControl.Contains("immutable", StringComparison.OrdinalIgnoreCase),
            $"SERVER_SPEC.md § 12.5: media bytes carry `immutable`. Got '{cacheControl}'.");
        Assert.True(cacheControl.Contains("max-age=31536000", StringComparison.OrdinalIgnoreCase),
            $"SERVER_SPEC.md § 12.5: media bytes carry max-age=31536000. Got '{cacheControl}'.");

        var tag = response.Header("ETag", "a still");

        // § 12.2: "The ETag is strong. No W/ prefix."
        Assert.False(tag.StartsWith("W/", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 12.2: the ETag is strong — two responses with the same ETag must be " +
            $"byte-identical forever, so a weak tag is not good enough. Got {tag}.");
        Assert.True(tag.StartsWith("\"", StringComparison.Ordinal) && tag.EndsWith("\"", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 12.2: the ETag is a quoted string. Got {tag}.");

        var inner = tag.Trim('"');
        Assert.True(inner.StartsWith("s720", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 12.2: the variant for a 720px JPEG still is 's720j'. Got '{inner}'.");

        var hex = inner[(inner.IndexOf('-') + 1)..];
        Assert.True(hex.Length == 32 && hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"SERVER_SPEC.md § 12.2: the ETag carries 32 lowercase hex characters (fingerprint[0..16)). " +
            $"Got '{hex}'.");
    }

    [Fact]
    public async Task If_none_match_returns_not_modified()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var first = await client.StillAsync(MediaFixtures.ThirdJpeg, 540);
        first.ShouldHaveStatus(200, "the first fetch");
        var tag = first.Header("ETag", "the first fetch");

        var second = await client.StillAsync(MediaFixtures.ThirdJpeg, 540, ifNoneMatch: tag);
        second.ShouldHaveStatus(304, "SERVER_SPEC.md § 12.2: an exact If-None-Match is 304");

        Assert.True(second.Body.Length == 0, "A 304 carries no body.");
        Assert.Equal(tag, second.HeaderOrNull("ETag"));
        Assert.True(second.HeaderOrNull("Cache-Control") is not null,
            "SERVER_SPEC.md § 12.2: a 304 returns the ETag and Cache-Control.");

        // § 12.2: "`*` matches any existing representation."
        var wildcard = await client.StillAsync(MediaFixtures.ThirdJpeg, 540, ifNoneMatch: "*");
        wildcard.ShouldHaveStatus(304, "SERVER_SPEC.md § 12.2: If-None-Match: * matches any existing representation");

        // A tag from a different variant must not match.
        var otherWidth = await client.StillAsync(MediaFixtures.ThirdJpeg, 1080, ifNoneMatch: tag);
        otherWidth.ShouldHaveStatus(200,
            "SERVER_SPEC.md § 12.2: the ETag names the variant, so a 540px tag must not satisfy a 1080px request");
    }

    [Fact]
    public async Task The_same_etag_always_means_the_same_bytes()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var first = await client.StillAsync(MediaFixtures.SecondJpeg, 720);
        var second = await client.StillAsync(MediaFixtures.SecondJpeg, 720);

        first.ShouldHaveStatus(200, "the first fetch");
        second.ShouldHaveStatus(200, "the second fetch");

        Assert.Equal(first.HeaderOrNull("ETag"), second.HeaderOrNull("ETag"));
        Assert.True(first.Body.SequenceEqual(second.Body),
            "SERVER_SPEC.md § 12.5: 'Bytes are immutable per ETag. The same ETag always means the same bytes.' " +
            $"Two fetches with the same ETag returned {first.Body.Length} and {second.Body.Length} bytes.");
    }

    [Fact]
    public async Task The_cache_busting_parameter_is_accepted_and_ignored()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var plain = await client.StillAsync(MediaFixtures.SecondJpeg, 720);
        plain.ShouldHaveStatus(200, "no v");

        // § 12.3: "The server MUST accept any value, MUST ignore it when selecting content, and MUST
        // NOT fail on a mismatch with the current mediaVersion."
        foreach (var version in new[] { "0000000000000000", "not-a-version", "", "🌅" })
        {
            var response = await client.StillAsync(MediaFixtures.SecondJpeg, 720, version: version);
            response.ShouldHaveStatus(200,
                $"SERVER_SPEC.md § 12.3: `v` is a cache buster the server must accept and ignore; v='{version}'");

            Assert.Equal(plain.HeaderOrNull("ETag"), response.HeaderOrNull("ETag"));
        }
    }

    [Fact]
    public async Task Meta_is_never_cached()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.MetaAsync(MediaFixtures.PlainJpeg);
        response.ShouldHaveStatus(200, "meta");

        var cacheControl = response.HeaderOrNull("Cache-Control");
        Assert.True(cacheControl is not null && cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase),
            "SERVER_SPEC.md § 12.5 point 5: `meta` and every other JSON endpoint are no-store. " +
            $"Got '{cacheControl ?? "(absent)"}'.");
    }

    // ---- wrong kind, unknown id, missing file -----------------------------------------------

    [Theory]
    [InlineData("still")]
    [InlineData("thumb")]
    public async Task A_still_endpoint_refuses_a_video(string endpoint)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.GetAsync($"/media/{Rm2Client.EncodeId(MediaFixtures.Video)}/{endpoint}");
        var failure = response.ShouldBeError("wrong_media_kind",
            $"SERVER_SPEC.md § 1.1 and § 12.1: /{endpoint} on a video is 409 wrong_media_kind. There are no " +
            "poster frames and the server never decodes a video frame.");

        Assert.Equal(MediaFixtures.Video, failure.Detail("id", "wrong_media_kind details").GetString());
        Assert.Equal("video", failure.Detail("kind", "wrong_media_kind details").GetString());
        Assert.Equal(endpoint, failure.Detail("endpoint", "wrong_media_kind details").GetString());
    }

    [Fact]
    public async Task The_video_endpoint_refuses_a_still()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.VideoAsync(MediaFixtures.PlainJpeg);
        var failure = response.ShouldBeError("wrong_media_kind",
            "SERVER_SPEC.md § 12.1: /video on a still is 409 wrong_media_kind");

        Assert.Equal("still", failure.Detail("kind", "wrong_media_kind details").GetString());
    }

    [Fact]
    public async Task An_id_that_is_not_a_record_is_an_unknown_media_id()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        const string absent = "never-scanned.jpg";
        foreach (var endpoint in new[] { "meta", "still", "thumb" })
        {
            var response = await client.GetAsync($"/media/{Rm2Client.EncodeId(absent)}/{endpoint}");
            var failure = response.ShouldBeError("unknown_media_id",
                $"SERVER_SPEC.md § 11.2 step 4: an id that is not a record in the open session is 404 " +
                $"unknown_media_id (/{endpoint})");

            Assert.Equal(absent, failure.Detail("id", "unknown_media_id details").GetString());
        }
    }

    [Fact]
    public async Task A_file_that_vanished_under_the_session_is_a_media_file_missing()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // § 7.4.8: the session never rescans. Deleting the file leaves the record in place.
        File.Delete(folder.File(MediaFixtures.SecondJpeg));

        var response = await client.StillAsync(MediaFixtures.SecondJpeg);
        var failure = response.ShouldBeError("media_file_missing",
            "SERVER_SPEC.md § 11.2 step 6 and § 11.3: the id is still a record but the file is gone, so this " +
            "is media_file_missing, not unknown_media_id. The difference tells the client to discard that " +
            "side rather than resync.");

        Assert.Equal(MediaFixtures.SecondJpeg, failure.Detail("id", "media_file_missing details").GetString());

        var before = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "after a failed media read");

        // § 11.3: "The server MUST NOT mutate session state from a GET. A failed media read never
        // drops a record on its own" — the client decides, because a lost image request is far more
        // likely to be a flaky Wi-Fi link than a bad file. So the id must still be a record, which
        // is exactly the difference between media_file_missing and unknown_media_id.
        (await client.MetaAsync(MediaFixtures.SecondJpeg)).ShouldBeError("media_file_missing",
            "SERVER_SPEC.md § 11.3: the record survives a failed read, so meta is still media_file_missing " +
            "rather than unknown_media_id. Only POST /session/discard drops a record.");

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200, "the session after two failed reads");
        Assert.Equal(before.Total, after.Total);
        Assert.Equal(before.PairSeq, after.PairSeq);
    }

    // ---- id syntax ---------------------------------------------------------------------------

    [Theory]
    [InlineData("..", "the parent directory")]
    [InlineData(".", "the current directory")]
    [InlineData("%2e%2e", "an encoded parent directory")]
    public async Task A_dot_segment_is_never_a_media_id(string id, string what)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.GetAsync($"/media/{id}/meta");
        response.ShouldBeErrorOneOf(
            $"SERVER_SPEC.md § 11.1 step 4: {what} is not a legal media id",
            "invalid_media_id", "media_outside_session", "not_found");
    }

    [Fact]
    public async Task An_id_carrying_a_separator_is_refused()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // § 11.1 step 5: %2F and %5C decode to separators and MUST be rejected. This is the
        // directory-traversal guard, and it is the reason /media is the one place the token does not
        // buy the whole filesystem.
        foreach (var id in new[] { "%2Fetc%2Fpasswd", "..%2F..%2Fetc%2Fpasswd", "sub%5Cfile.jpg", "%2Ftmp%2Fx.jpg" })
        {
            var response = await client.GetAsync($"/media/{id}/meta");
            response.ShouldBeErrorOneOf(
                $"SERVER_SPEC.md § 11.1 step 5 and § 11.2: '{id}' decodes to a path with a separator and must " +
                "not resolve. A token buys the session folder, not the filesystem.",
                "invalid_media_id", "media_outside_session", "not_found", "unknown_media_id");
        }
    }

    [Fact]
    public async Task An_absolute_path_as_an_id_is_refused()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var outside = Path.Combine(Path.GetTempPath(), "rm2-outside.jpg");
        await File.WriteAllBytesAsync(outside, MediaFixtures.Jpeg(64, 64));

        try
        {
            var response = await client.GetAsync($"/media/{Rm2Client.EncodeId(outside)}/still");
            response.ShouldBeErrorOneOf(
                "SERVER_SPEC.md § 11.1 step 4: a rooted id is not a legal media id, and § 11.2 step 5 is the " +
                "backstop — the resolved directory must be exactly the session folder",
                "invalid_media_id", "media_outside_session", "unknown_media_id", "not_found");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task An_id_that_is_not_valid_utf8_is_refused()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // A lone continuation byte: %FF is not a valid UTF-8 sequence.
        var response = await client.GetAsync("/media/%FF%FE.jpg/meta");
        var failure = response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 11.1 step 2: a byte sequence that is not valid UTF-8 is 400 invalid_media_id",
            "invalid_media_id", "unknown_media_id", "not_found");

        if (failure.Code == "invalid_media_id")
            Assert.Equal("not-utf8", failure.Detail("reason", "invalid_media_id details").GetString());
    }

    [Fact]
    public async Task An_id_over_two_hundred_and_fifty_five_units_is_refused()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var oversized = new string('a', 300) + ".jpg";
        var response = await client.MetaAsync(oversized);

        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 11.1 step 4 and § 15: a media id is capped at 255 UTF-16 code units",
            "invalid_media_id", "unknown_media_id");
    }

    [Fact]
    public async Task A_file_with_a_disallowed_extension_is_refused_even_though_it_exists()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // notes.txt is really there, and it is really not media. § 11.2 step 3 checks the extension
        // before membership, so this is 403 rather than 404 — "redundant with step 4 in practice; it
        // MUST be checked anyway".
        var response = await client.MetaAsync(MediaFixtures.NotMedia);
        var failure = response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 11.2 step 3: an extension in neither list of SPEC.md § Media policy is " +
            "403 media_extension_not_allowed",
            "media_extension_not_allowed", "unknown_media_id");

        if (failure.Code == "media_extension_not_allowed")
            Assert.Equal("txt", failure.Detail("extension", "media_extension_not_allowed details")
                .GetString()?.TrimStart('.'));
    }

    // ---- awkward names -----------------------------------------------------------------------

    [Theory]
    [InlineData(MediaFixtures.SpacesAndHash)]
    [InlineData(MediaFixtures.Unicode)]
    [InlineData(MediaFixtures.Astral)]
    [InlineData(MediaFixtures.UppercaseExtension)]
    public async Task An_awkward_filename_round_trips_through_the_url(string id)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.MetaAsync(id);
        response.ShouldHaveStatus(200,
            $"SERVER_SPEC.md § 11.1: '{id}' percent-encodes to one path segment and decodes back to itself. " +
            $"Encoded: {Rm2Client.EncodeId(id)}");

        // § 11.1 step 6: "The server MUST return the on-disk spelling", not the spelling sent.
        Assert.Equal(id, response.JsonBody.GetProperty("id").GetString());
        output.WriteLine($"{id} → {Rm2Client.EncodeId(id)} → ok");
    }

    [Fact]
    public async Task A_space_must_be_percent_twenty_and_not_a_plus()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // § 11.1 step 1: "A space MUST be %20. `+` MUST NOT be used for a space — this is a path
        // segment, not a query." A server that decodes '+' as a space would resolve a file the
        // client never named.
        var withPlus = MediaFixtures.SpacesAndHash.Replace(" ", "+");
        var response = await client.GetAsync($"/media/{Uri.EscapeDataString(withPlus)}/meta");

        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 11.1 step 1: '+' is not a space in a path segment, so an id spelled with '+' " +
            "must not resolve to the file spelled with a space",
            "unknown_media_id", "invalid_media_id", "media_file_missing");
    }

    [Fact]
    public async Task Two_names_differing_only_by_case_are_distinguished_as_the_filesystem_does()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var mixed = await client.MetaAsync(MediaFixtures.MixedCase);
        mixed.ShouldHaveStatus(200, $"'{MediaFixtures.MixedCase}' is in the folder");
        Assert.Equal(MediaFixtures.MixedCase, mixed.JsonBody.GetProperty("id").GetString());

        var lower = await client.MetaAsync(MediaFixtures.LowerCase);

        if (LibraryFolder.FilesystemIsCaseSensitive)
        {
            // Both files exist and they are different records. § 11.1 step 3: comparison is ordinal
            // and case-sensitive off Windows, "because they are different filenames on Linux".
            lower.ShouldHaveStatus(200, $"'{MediaFixtures.LowerCase}' is a separate file on this filesystem");
            Assert.Equal(MediaFixtures.LowerCase, lower.JsonBody.GetProperty("id").GetString());

            Assert.True(mixed.JsonBody.GetProperty("sizeBytes").GetInt64() !=
                        lower.JsonBody.GetProperty("sizeBytes").GetInt64(),
                "SERVER_SPEC.md § 11.1 step 3: on a case-sensitive filesystem these are two different files, " +
                "and the server must not fold them together.");
        }
        else
        {
            // One file. § 11.1 step 3 makes the match case-insensitive here, and step 6 requires the
            // on-disk spelling back regardless of what was asked for.
            lower.ShouldHaveStatus(200,
                "SERVER_SPEC.md § 11.1 step 3: on a case-insensitive filesystem the comparison is " +
                "OrdinalIgnoreCase, matching JsonCatalog's dictionary and the platform");

            Assert.Equal(MediaFixtures.MixedCase, lower.JsonBody.GetProperty("id").GetString());
        }
    }

    // ---- undecodable bytes ---------------------------------------------------------------------

    [Theory]
    [InlineData(MediaFixtures.Corrupt, "a JPEG header followed by noise")]
    [InlineData(MediaFixtures.ZeroByte, "a zero-byte file")]
    public async Task A_file_that_will_not_decode_is_unprocessable_rather_than_a_crash(string id, string what)
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(id, 720);
        var failure = response.ShouldBeError("media_decode_failed",
            $"SERVER_SPEC.md § 5.5 and § 12.1: {what} is present but is not a decodable image, which is " +
            "422 media_decode_failed. SPEC.md § Media policy requires that this does not crash the session.");

        Assert.Equal(id, failure.Detail("id", "media_decode_failed details").GetString());

        // The session has to survive it: the point of 422 rather than 500.
        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200,
            $"the session survives a request for {what}");
        Assert.Equal("ranking", after.State);
    }

    /// <summary>
    /// A truncated JPEG is the case the contract does not decide. § 5.5 gives 422 for a file that
    /// "is not a decodable image", but a JPEG cut off mid-scan has intact headers and a partial
    /// scan: most decoders return the rows they got. Serving those rows and refusing outright are
    /// both defensible, and SPEC.md § Media policy only insists the session survives — so that is
    /// what this asserts, plus the rule that whichever answer comes back is a legal one.
    /// </summary>
    [Fact]
    public async Task A_truncated_file_is_either_decoded_or_refused_but_never_crashes_the_session()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.StillAsync(MediaFixtures.Truncated, 720);

        if (response.StatusCode == 200)
        {
            Assert.True(response.Body.Length > 0,
                "A 200 must carry the bytes it decoded; an empty body is neither an image nor an error.");
            Assert.True(response.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
                $"A decoded still is an image. Got '{response.ContentType}'.");
            output.WriteLine($"{MediaFixtures.Truncated}: decoded the partial scan, {response.Body.Length} bytes");
        }
        else
        {
            response.ShouldBeError("media_decode_failed",
                "SERVER_SPEC.md § 5.5: a file that cannot be decoded is 422 media_decode_failed — never a 500");
            output.WriteLine($"{MediaFixtures.Truncated}: refused as undecodable");
        }

        var after = (await client.GetSessionAsync()).ShouldBeSnapshot(200,
            "SPEC.md § Media policy: an unreadable file is skipped for that pair and the session does not crash");
        Assert.Equal("ranking", after.State);
    }

    [Fact]
    public async Task A_wide_gamut_source_is_converted_rather_than_refused()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        foreach (var id in new[] { MediaFixtures.WideGamut, MediaFixtures.WideGamutPng })
        {
            var response = await client.StillAsync(id, 360);
            response.ShouldHaveStatus(200,
                $"SERVER_SPEC.md § 12.3: '{id}' carries an ICC profile; the profile is applied during decode " +
                "and the output is sRGB. A profile is not a reason to fail.");

            Assert.True(response.Body.Length > 0, $"'{id}' rendered an empty body.");
            output.WriteLine($"{id}: {response.Body.Length} bytes, {response.ContentType}");
        }
    }

    [Fact]
    public async Task A_very_large_source_is_served_without_exhausting_memory()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        // 4000×3000 is 12 megapixels — 36 MB as raw RGB, and more than that if decoded to a
        // 32-bit buffer. SPEC.md § Pipeline requires decoding at the target size rather than at
        // full resolution; a server that decodes whole and then resizes will still pass this, but
        // one that holds several of them will not.
        var response = await client.StillAsync(MediaFixtures.Large, 720);
        response.ShouldHaveStatus(200, $"SERVER_SPEC.md § 12.3: the {MediaFixtures.LargeSize.Width}×" +
                                       $"{MediaFixtures.LargeSize.Height} fixture at w=720");

        Assert.True(response.Body.Length > 0, "The large still rendered an empty body.");
        output.WriteLine($"{MediaFixtures.Large} at w=720: {response.Body.Length} bytes");

        var meta = await client.MetaAsync(MediaFixtures.Large);
        meta.ShouldHaveStatus(200, "meta for the large fixture");
        Assert.Equal(MediaFixtures.LargeSize.Width, meta.JsonBody.GetProperty("width").GetInt32());
        Assert.Equal(MediaFixtures.LargeSize.Height, meta.JsonBody.GetProperty("height").GetInt32());
    }

    // ---- video and ranges ---------------------------------------------------------------------

    [Fact]
    public async Task The_video_endpoint_returns_the_original_bytes_unmodified()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var onDisk = await File.ReadAllBytesAsync(folder.File(MediaFixtures.Video));

        var response = await client.VideoAsync(MediaFixtures.Video);
        response.ShouldHaveStatus(200, "SERVER_SPEC.md § 12.4: the whole video");

        Assert.True(response.Body.SequenceEqual(onDisk),
            "SERVER_SPEC.md § 12.4 and SERVER_PLAN.md § 3.4: 'The bytes are the original file, unmodified.' " +
            $"Got {response.Body.Length} bytes for a {onDisk.Length}-byte file.");

        Assert.Equal("bytes", response.HeaderOrNull("Accept-Ranges"));
        Assert.True(response.ContentType?.StartsWith("video/x-msvideo", StringComparison.OrdinalIgnoreCase) == true,
            $"SERVER_SPEC.md § 12.4: Content-Type is by extension; .avi is video/x-msvideo. " +
            $"Got '{response.ContentType}'.");
    }

    [Fact]
    public async Task A_closed_range_returns_exactly_those_bytes()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var onDisk = await File.ReadAllBytesAsync(folder.File(MediaFixtures.Video));

        var response = await client.VideoAsync(MediaFixtures.Video, range: "bytes=100-199");
        response.ShouldHaveStatus(206, "SERVER_SPEC.md § 12.4: one satisfiable range is a 206");

        Assert.Equal(100, response.Body.Length);
        Assert.True(response.Body.SequenceEqual(onDisk[100..200]),
            "SERVER_SPEC.md § 12.4: the range must be the bytes it names.");

        Assert.Equal($"bytes 100-199/{onDisk.Length}", response.HeaderOrNull("Content-Range"));
    }

    [Fact]
    public async Task Open_ended_and_suffix_ranges_are_both_supported()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var onDisk = await File.ReadAllBytesAsync(folder.File(MediaFixtures.Video));
        var size = onDisk.Length;

        var openEnded = await client.VideoAsync(MediaFixtures.Video, range: "bytes=500-");
        openEnded.ShouldHaveStatus(206, "SERVER_SPEC.md § 12.4: open-ended ranges are supported");
        Assert.True(openEnded.Body.SequenceEqual(onDisk[500..]),
            "'bytes=500-' means from 500 to the end.");
        Assert.Equal($"bytes 500-{size - 1}/{size}", openEnded.HeaderOrNull("Content-Range"));

        var suffix = await client.VideoAsync(MediaFixtures.Video, range: "bytes=-500");
        suffix.ShouldHaveStatus(206, "SERVER_SPEC.md § 12.4: suffix ranges are supported");
        Assert.True(suffix.Body.SequenceEqual(onDisk[^500..]),
            "'bytes=-500' means the last 500 bytes, not the first 500.");
        Assert.Equal($"bytes {size - 500}-{size - 1}/{size}", suffix.HeaderOrNull("Content-Range"));
    }

    [Fact]
    public async Task A_multi_range_request_is_answered_with_the_first_range_only()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.VideoAsync(MediaFixtures.Video, range: "bytes=0-99,200-299");
        response.ShouldHaveStatus(206,
            "SERVER_SPEC.md § 12.4: several ranges are served as a single 206 covering only the first. " +
            "Multipart byte ranges MUST NOT be produced.");

        Assert.Equal(100, response.Body.Length);
        Assert.True(response.ContentType?.Contains("multipart", StringComparison.OrdinalIgnoreCase) != true,
            $"SERVER_SPEC.md § 12.4: multipart/byteranges MUST NOT be produced. Got '{response.ContentType}'.");
    }

    [Fact]
    public async Task An_unsatisfiable_range_reports_the_size()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var size = new FileInfo(folder.File(MediaFixtures.Video)).Length;

        var response = await client.VideoAsync(MediaFixtures.Video, range: $"bytes={size + 1000}-{size + 2000}");
        var failure = response.ShouldBeError("range_not_satisfiable",
            "SERVER_SPEC.md § 12.4: a range past the end of the file is 416");

        Assert.Equal(size, failure.Detail("sizeBytes", "range_not_satisfiable details").GetInt64());
        Assert.Equal($"bytes */{size}", response.HeaderOrNull("Content-Range"));
    }

    [Fact]
    public async Task A_syntactically_invalid_range_is_ignored()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var size = new FileInfo(folder.File(MediaFixtures.Video)).Length;

        foreach (var range in new[] { "bytes=abc-def", "chunks=0-10", "bytes=", "nonsense" })
        {
            var response = await client.VideoAsync(MediaFixtures.Video, range: range);
            response.ShouldHaveStatus(200,
                $"SERVER_SPEC.md § 12.4: a syntactically invalid Range ('{range}') is ignored and the full " +
                "body is served, per RFC 9110");

            Assert.Equal(size, response.Body.Length);
        }
    }

    [Fact]
    public async Task If_range_decides_between_a_range_and_the_whole_file()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var size = new FileInfo(folder.File(MediaFixtures.Video)).Length;

        var probe = await client.VideoAsync(MediaFixtures.Video);
        var tag = probe.Header("ETag", "the video (SERVER_SPEC.md § 12.2)");

        var matching = await client.VideoAsync(MediaFixtures.Video, range: "bytes=0-99", ifRange: tag);
        matching.ShouldHaveStatus(206, "SERVER_SPEC.md § 12.4: If-Range matching the ETag serves the range");
        Assert.Equal(100, matching.Body.Length);

        var mismatched = await client.VideoAsync(MediaFixtures.Video, range: "bytes=0-99",
                                                 ifRange: "\"orig-00000000000000000000000000000000\"");
        mismatched.ShouldHaveStatus(200,
            "SERVER_SPEC.md § 12.4: If-Range that does not match yields 200 with the full body — the client's " +
            "cached prefix is stale, so a range on top of it would be corrupt");
        Assert.Equal(size, mismatched.Body.Length);
    }

    [Fact]
    public async Task The_video_variant_is_orig()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var response = await client.VideoAsync(MediaFixtures.Video);
        var tag = response.Header("ETag", "the video").Trim('"');

        Assert.True(tag.StartsWith("orig-", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 12.2: the variant for the video endpoint is 'orig'. Got '{tag}'.");
    }

    // ---- no session --------------------------------------------------------------------------

    [Fact]
    public async Task There_is_no_way_to_fetch_bytes_without_a_session()
    {
        var client = await ClientAsync();

        // § 11.2 step 1: "No session open → 404 no_session. There is no way to fetch bytes without
        // a session." This is checked first, before the id is even looked at.
        foreach (var endpoint in new[] { "meta", "still", "thumb", "video" })
        {
            var response = await client.GetAsync($"/media/{Rm2Client.EncodeId(MediaFixtures.PlainJpeg)}/{endpoint}");
            response.ShouldBeError("no_session",
                $"SERVER_SPEC.md § 11.2 step 1: /media/{{id}}/{endpoint} with no session open is 404 no_session");
        }
    }

    [Fact]
    public async Task The_links_a_snapshot_hands_out_already_carry_the_version()
    {
        var (folder, client) = await OpenMenagerieAsync();
        using var _ = folder;

        var snapshot = (await client.GetPairAsync()).ShouldBeSnapshot(200, "the pair");
        var reference = snapshot.Left;

        // § 12.5 point 2: "The links the server hands out always include v=<mediaVersion>; a client
        // that uses links verbatim has genuinely immutable URLs."
        foreach (var (name, link) in new[] { ("still", reference.StillLink), ("thumb", reference.ThumbLink) })
        {
            Assert.True(link is not null && link.Contains($"v={reference.MediaVersion}", StringComparison.Ordinal),
                $"SERVER_SPEC.md § 9.3 and § 12.5: links.{name} must already carry v=<mediaVersion>, which is " +
                $"what makes `immutable` honest. mediaVersion is '{reference.MediaVersion}', link is '{link}'.");

            var response = await client.FollowAsync(link!);
            response.ShouldHaveStatus(200, $"SERVER_SPEC.md § 9.3: links.{name} is usable verbatim");
        }

        Assert.True(reference.MetaLink.StartsWith("/api/v1/media/", StringComparison.Ordinal),
            $"SERVER_SPEC.md § 9.3: links are server-built paths under the API base. Got '{reference.MetaLink}'.");
    }
}
