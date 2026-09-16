namespace RankMaster2.Pc.Video;

/// <summary>
/// Pure: fit a source frame size into a pane, without upscaling, with the even-dimension and
/// 32-byte-pitch rules VLC's <c>vmem</c> output needs. Ported unchanged from the old app's
/// <c>VlcFramePlayer.Fit</c> / <c>Align32</c> (see D-video.md § 4.1, § 4.7).
/// </summary>
public static class PaneFit
{
    /// <summary>Shrinks (source, never grows it) to fit within (paneWidth, paneHeight), then floors to
    /// even dimensions of at least 2. A pane smaller than 16px in either dimension is treated as 16px,
    /// matching the old app's guard against a not-yet-laid-out control.</summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int paneWidth, int paneHeight)
    {
        var maxW = Math.Max(16, paneWidth);
        var maxH = Math.Max(16, paneHeight);

        double w = Math.Max(1, sourceWidth);
        double h = Math.Max(1, sourceHeight);

        if (w > maxW || h > maxH)
        {
            var scale = Math.Min(maxW / w, maxH / h);
            w = Math.Round(w * scale);
            h = Math.Round(h * scale);
        }

        var width = Math.Max(2, (int)w & ~1);
        var height = Math.Max(2, (int)h & ~1);
        return (width, height);
    }

    /// <summary>Rounds up to the next multiple of 32 — the pitch/line alignment VLC's RV32 vout wants.</summary>
    public static int Align32(int size) => (size + 31) & ~31;
}
