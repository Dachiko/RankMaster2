namespace RankMaster2.Server.Media;

/// <summary>
/// Everything the media layer can be tuned with. Bound from the <c>Media</c> configuration
/// section. The defaults are the contract's defaults; nothing here changes a response body,
/// only where bytes are cached and how much memory a decode may take.
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// Where re-encoded stills are cached. SERVER_SPEC.md § 12.5.6: this lives under the
    /// server's own data directory and <b>never</b> inside the user's media folder. Leave null
    /// for <see cref="DefaultCacheDirectory"/>.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Upper bound on the still cache. Oldest-accessed entries are evicted first.</summary>
    public long CacheMaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// JPEG quality. SERVER_SPEC.md § 12.3: "Quality is a server setting; it MUST be identical
    /// across requests, because the ETag does not encode it." Changing it therefore invalidates
    /// nothing client-side; it is folded into the cache key so the server at least never serves
    /// its own stale bytes.
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>WebP quality. Same caveat as <see cref="JpegQuality"/>.</summary>
    public int WebpQuality { get; set; } = 80;

    /// <summary>
    /// How many stills may be decoded at once. SPEC.md § Pipeline keeps one sequential reader for
    /// exactly this reason: two large decodes at once is how a ~100-150 MB budget dies. Cache
    /// hits do not take a slot, so this only serialises cold renders.
    /// </summary>
    public int MaxConcurrentDecodes { get; set; } = 2;

    /// <summary>
    /// Hard ceiling on the pixel buffers one decode may hold at once, enforced by
    /// <see cref="DecodeBudget"/> before the memory is taken rather than discovered afterwards.
    /// A reduced-size decode of a 48 MP JPEG to 1080 px peaks around 12 MB, so this is roughly ten
    /// times the working figure and still well inside the server's budget. A decode that would
    /// exceed it is refused and reported as <c>media_decode_failed</c>.
    /// <para/>
    /// The headroom is for the formats with no sampled decode. JPEG scales during the IDCT, so it
    /// is cheap at any target; PNG, BMP and GIF have no equivalent and must be decoded whole,
    /// which is one buffer of width × height × 4. At this ceiling that caps a non-JPEG still at
    /// about 32 megapixels.
    /// </summary>
    public int DecodeMemoryLimitMegabytes { get; set; } = 128;

    /// <summary>
    /// The server's own data directory. Not named in SERVER_SPEC.md; chosen here as the platform
    /// per-user local application data, which is writable without elevation on every target.
    /// </summary>
    public static string DefaultCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(AppContext.BaseDirectory, "data");

        return Path.Combine(local, "RankMaster2", "Server", "cache", "still");
    }

    public string ResolvedCacheDirectory() =>
        string.IsNullOrWhiteSpace(CacheDirectory) ? DefaultCacheDirectory() : CacheDirectory!;
}
