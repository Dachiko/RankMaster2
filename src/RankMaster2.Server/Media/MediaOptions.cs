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
    /// A decode that would exceed it is refused and reported as <c>media_decode_failed</c> with
    /// <c>details.reason = "no_room"</c>.
    ///
    /// <para><b>512 MB, raised from 128 on 2026-09-17, because 128 was the wrong number.</b> The old
    /// figure was reasoned from JPEG and it is right about JPEG: JPEG scales during the IDCT, so a
    /// 48 MP file decoded to 1080 px peaks around 12 MB and never comes close. But PNG, BMP and GIF
    /// have no sampled decode — they are decoded whole, one buffer of width × height × 4, plus the
    /// resample buffer held alongside it. At 128 MB that is about **30 effective megapixels**, so a
    /// 6000 × 6000 PNG was refused outright even with the whole server to itself, and a *pair* of
    /// 4000 × 4000 ones failed every time. That is AUDIT2.md § 2.1, measured at 12 refusals out of
    /// 12 pairs, and the owner met it as "This phone cannot open this file" on a photograph that was
    /// perfectly fine.</para>
    ///
    /// <para>512 MB takes the non-JPEG limit to about 128 megapixels, past any camera he is likely to
    /// own, while still being a bounded ceiling that refuses a decode bomb rather than meeting it.
    /// It is not extravagant: this is a desktop process on his own PC, and the Windows client sitting
    /// beside it has used 512 MB from the start (<c>Composition.cs</c>). The two halves of the same
    /// product disagreeing by a factor of four was the actual defect — and the server's was the half
    /// the phone depends on entirely.</para>
    ///
    /// <para>Raising it does not make two large decodes race for memory: <see cref="DecodeRoom"/>
    /// makes a pair that will not fit together take turns instead of failing, so this number only has
    /// to accommodate the largest <i>single</i> picture, never the sum of a pair.</para>
    /// </summary>
    public int DecodeMemoryLimitMegabytes { get; set; } = 512;

    /// <summary>
    /// SERVER_SPEC.md § 2.4: the server data directory is <c>RankMaster2/Server</c> — one spelling,
    /// everywhere, capital <c>R</c>, <c>M</c>, <c>S</c> — and the rendered-still cache lives at
    /// <c>&lt;data dir&gt;/cache</c> under it. <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// already resolves to the platform's per-user data root (<c>%LOCALAPPDATA%</c> on Windows,
    /// <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c> elsewhere), which is writable without
    /// elevation on every target — the same root § 2.4 names for the platform default.
    /// <para/>
    /// C12: this used to add its own <c>still</c> subdirectory, which § 2.4 does not call for and
    /// which put the cache one level away from where the tray, the token store and the
    /// certificate live.
    /// </summary>
    public static string DefaultCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(AppContext.BaseDirectory, "data");

        return Path.Combine(local, "RankMaster2", "Server", "cache");
    }

    public string ResolvedCacheDirectory() =>
        string.IsNullOrWhiteSpace(CacheDirectory) ? DefaultCacheDirectory() : CacheDirectory!;
}
