using System.Net;
using RankMaster2.Server.Media;
using RankMaster2.Server.Tests.Fixtures;
using SkiaSharp;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// Regression tests for the second independent audit's four findings assigned to this package:
/// AUDIT2.md § 1.2, § 3.5, § 3.11 and § 3.12. Each of these failed against the tree as the audit
/// found it (verified by running them before the corresponding fix went in) and passes now.
/// </summary>
public sealed class SecondAuditFixTests
{
    private static string Url(string id, string verb, string query = "") =>
        $"/api/v1/media/{MediaIdCodec.Encode(id)}/{verb}{query}";

    // ---------------------------------------------------------- § 1.2

    /// <summary>
    /// The audit's own reproduction shape, over real HTTP through the real endpoint pipeline and
    /// the real disk cache (one <see cref="MediaHost"/>, so one <see cref="StillCache"/> instance
    /// backs both requests): two folders, each holding a "holiday.jpg" of the identical byte
    /// length and the identical mtime, one near-black and one near-white. Before § 1.2's fix,
    /// <see cref="MediaFingerprint"/> never hashed the folder, so folder B's request hit folder A's
    /// entry in the shared disk cache (keyed by the fingerprint's <c>EntityHex</c>,
    /// <c>MediaEndpoints.cs</c>) and got back folder A's ETag and folder A's pixels. After the fix
    /// the two folders no longer share a fingerprint, so neither the ETag nor the served bytes
    /// collide.
    /// </summary>
    [Fact]
    public async Task DifferentFolders_SameNameSizeAndMtime_DoNotServeEachOthersBytes()
    {
        using var folderA = new StubSession();
        using var folderB = new StubSession();

        var black = SolidColourJpeg(gray: 10);
        var white = SolidColourJpeg(gray: 245);
        var commonSize = Math.Max(black.Length, white.Length);
        black = PadTo(black, commonSize);
        white = PadTo(white, commonSize);

        var mtime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var pathA = folderA.Add("holiday.jpg", black);
        File.SetLastWriteTimeUtc(pathA, mtime);
        var pathB = folderB.Add("holiday.jpg", white);
        File.SetLastWriteTimeUtc(pathB, mtime);

        // The audit's own precondition: same name, same byte length, same mtime, different bytes.
        Assert.Equal("holiday.jpg", Path.GetFileName(pathA));
        Assert.Equal("holiday.jpg", Path.GetFileName(pathB));
        Assert.Equal(new FileInfo(pathA).Length, new FileInfo(pathB).Length);
        Assert.Equal(File.GetLastWriteTimeUtc(pathA), File.GetLastWriteTimeUtc(pathB));

        var switcher = new SwitchableSession();
        await using var host = new MediaHost(switcher);
        await host.Cache.WhenScanned;

        switcher.Current = folderA;
        var respA = await host.Client.GetAsync(Url("holiday.jpg", "still", "?w=360"));
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var etagA = respA.Headers.ETag?.Tag;
        var pixelsA = SKBitmap.Decode(await respA.Content.ReadAsByteArrayAsync());

        switcher.Current = folderB;
        var respB = await host.Client.GetAsync(Url("holiday.jpg", "still", "?w=360"));
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var etagB = respB.Headers.ETag?.Tag;
        var pixelsB = SKBitmap.Decode(await respB.Content.ReadAsByteArrayAsync());

        // Before the fix this failed: etagA == etagB and pixelsB was folder A's black picture.
        Assert.NotEqual(etagA, etagB);

        var centreA = pixelsA.GetPixel(pixelsA.Width / 2, pixelsA.Height / 2);
        var centreB = pixelsB.GetPixel(pixelsB.Width / 2, pixelsB.Height / 2);
        Assert.True(centreA.Red < 60, $"folder A should still decode near-black; got R={centreA.Red}");
        Assert.True(centreB.Red > 200, $"folder B should decode near-white, not folder A's black; got R={centreB.Red}");
    }

    /// <summary>Same shape as above, at the unit level, pinned separately in MediaFingerprintTests.</summary>
    [Fact]
    public void Unit_DifferentFolder_ChangesTheFingerprint()
    {
        var a = MediaFingerprint.Of("holiday.jpg", 9000, 123456789L, "/library/2024");
        var b = MediaFingerprint.Of("holiday.jpg", 9000, 123456789L, "/library/2025");
        Assert.NotEqual(a.EntityHex, b.EntityHex);
    }

    private static byte[] SolidColourJpeg(byte gray, int width = 96, int height = 96)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(gray, gray, gray));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Grows a JPEG to an exact target length by appending zero bytes after its EOI marker
    /// (0xFFD9) — trailing bytes past EOI are not part of the image and every decoder, SkiaSharp
    /// included, ignores them. Lets two different-content JPEGs be forced to the identical byte
    /// length the audit's reproduction needs, deterministically, without hunting for a quality
    /// setting that happens to match.
    /// </summary>
    private static byte[] PadTo(byte[] jpeg, int length)
    {
        if (jpeg.Length == length)
            return jpeg;

        if (jpeg.Length > length)
            throw new ArgumentOutOfRangeException(nameof(length), "Target shorter than the source JPEG.");

        var padded = new byte[length];
        jpeg.CopyTo(padded, 0);
        return padded;
    }

    // ---------------------------------------------------------- § 3.5

    /// <summary>
    /// SERVER_RUNNING.md lists <c>cache\</c> as one of the files under the data directory. Before
    /// the fix, <c>MediaOptions.ResolvedCacheDirectory</c> never consulted
    /// <c>RankMaster2:DataDirectory</c> at all, so an operator who moved the data directory (the
    /// only reason anyone does) kept filling the old drive with cached stills regardless.
    /// </summary>
    [Fact]
    public async Task DefaultCacheDirectory_FollowsTheConfiguredDataDirectory()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataDir);
        try
        {
            using var session = new StubSession();
            session.Add("alpha.jpg", MediaFixtures.Jpeg(64, 64));

            // CacheDirectory deliberately left unset: this is the default-resolution path § 3.5
            // is about, not an operator override.
            var options = new MediaOptions();

            var configuration = new Dictionary<string, string?>
            {
                ["RankMaster2:DataDirectory"] = dataDir,
            };

            await using var host = new MediaHost(session, options, configuration);
            await host.Cache.WhenScanned;

            // Force the cache to actually write something.
            var response = await host.Client.GetAsync(Url("alpha.jpg", "still", "?w=360"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.StartsWith(dataDir, host.CacheDirectory);
            Assert.Equal(Path.Combine(dataDir, "cache"), host.CacheDirectory);
            Assert.True(Directory.Exists(host.CacheDirectory), "the cache should have written under the configured data directory");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>An explicit Media:CacheDirectory is still obeyed over the data directory.</summary>
    [Fact]
    public async Task ExplicitCacheDirectory_StillWinsOverTheDataDirectory()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-data-" + Guid.NewGuid().ToString("N")[..8]);
        var explicitCache = Path.Combine(Path.GetTempPath(), "rm2-explicit-cache-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var session = new StubSession();
            var options = new MediaOptions { CacheDirectory = explicitCache };
            var configuration = new Dictionary<string, string?> { ["RankMaster2:DataDirectory"] = dataDir };

            await using var host = new MediaHost(session, options, configuration);

            Assert.Equal(explicitCache, host.CacheDirectory);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(explicitCache, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------- § 3.11

    /// <summary>
    /// A trailing slash on a media URL used to answer 403 <c>media_extension_not_allowed</c>,
    /// naming <c>id="still"</c>, <c>extension=""</c> — a file nobody asked for. Routing tolerates
    /// the slash and resolves <c>id=alpha.jpg</c> as normal; <c>MediaEndpoints.RawIdSegment</c> did
    /// not agree with it. <see cref="MediaHost"/> fills in <c>RawTarget</c> the way a real Kestrel
    /// server does, so this exercises the exact code path the audit found untestable through
    /// <c>TestServer</c> by default.
    /// </summary>
    [Fact]
    public async Task TrailingSlashOnStill_StillResolvesTheId_NotTheVerb()
    {
        using var session = new StubSession();
        session.Add("alpha.jpg", MediaFixtures.Jpeg(64, 64));
        await using var host = new MediaHost(session);

        var withoutSlash = await host.Client.GetAsync(Url("alpha.jpg", "still", "?w=360"));
        Assert.Equal(HttpStatusCode.OK, withoutSlash.StatusCode);

        var withTrailingSlash = await host.Client.GetAsync(
            $"/api/v1/media/{MediaIdCodec.Encode("alpha.jpg")}/still/?w=360");

        // Before the fix: 403 media_extension_not_allowed, details { id: "still", extension: "" }.
        Assert.Equal(HttpStatusCode.OK, withTrailingSlash.StatusCode);
        Assert.Equal(
            await withoutSlash.Content.ReadAsByteArrayAsync(),
            await withTrailingSlash.Content.ReadAsByteArrayAsync());
    }

    // ---------------------------------------------------------- § 3.12

    /// <summary>
    /// <c>StillCache.GetOrAddAsync</c> writes an entry, runs its own eviction, and only then
    /// returns the path. With <see cref="MediaOptions.CacheMaxBytes"/> set below the size of the
    /// still it is about to write, the entry it just added is — deterministically, no concurrency
    /// needed — also the one <c>EvictIfOverAsync</c> immediately evicts, so the file is already
    /// gone from disk by the time <c>MediaEndpoints.StillAsync</c> stats it. Before the fix that
    /// surfaced as 404 <c>media_file_missing</c> for a photograph that was never missing; after the
    /// fix the handler falls back to rendering it directly and still answers 200.
    /// </summary>
    [Fact]
    public async Task StillCacheEvictingItsOwnFreshEntry_StillAnswers200()
    {
        using var session = new StubSession();
        session.Add("alpha.jpg", MediaFixtures.Jpeg(64, 64));

        var options = new MediaOptions
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "rm2-tiny-cache-" + Guid.NewGuid().ToString("N")[..8]),
            CacheMaxBytes = 1, // smaller than any rendered still: self-evicts before GetOrAddAsync returns.
        };

        await using var host = new MediaHost(session, options);
        await host.Cache.WhenScanned;

        var response = await host.Client.GetAsync(Url("alpha.jpg", "still", "?w=360"));

        // Before the fix: 404 media_file_missing, { id: "alpha.jpg" } — the file was on disk the
        // whole time; only the cache's copy of it was gone.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);

        // The eviction really did run, so this is exercising the fallback and not a bound that
        // quietly never took effect.
        Assert.Equal(0, host.Cache.Count);
    }
}
