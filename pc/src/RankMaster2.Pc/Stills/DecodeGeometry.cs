using SkiaSharp;

namespace RankMaster2.Pc.Stills;

/// <summary>
/// Pure geometry: no I/O, no Skia decode, only arithmetic and the pieces of
/// <c>SKCodec</c>'s API surface that are themselves pure (<see cref="ChooseScaledDimensions"/>
/// reads <c>codec.Info</c> and calls <c>codec.GetScaledDimensions</c>, neither of which decodes a
/// pixel). Everything marked "copied" is lifted verbatim (arithmetic unchanged) from
/// <c>src/RankMaster2.Server/Media/StillRenderer.cs</c>, per pc/plans/C-stills.md section 4.
/// </summary>
public static class DecodeGeometry
{
    /// <summary>
    /// The old <c>StillDecoder.Fit</c>: fits <paramref name="srcW"/> x <paramref name="srcH"/>
    /// (the DISPLAYED source size, i.e. after orientation) into the pane box
    /// <paramref name="paneW"/> x <paramref name="paneH"/>, never upscaling, and caps the long
    /// edge of the result at 4096 (plan section 1, "Long-edge cap"). Plan section 3.1 step 2.
    /// </summary>
    public static (int Width, int Height) Fit(int srcW, int srcH, int paneW, int paneH)
    {
        if (srcW <= 0 || srcH <= 0)
            return (Math.Max(1, srcW), Math.Max(1, srcH));

        var scale = Math.Min(Math.Min(paneW / (double)srcW, paneH / (double)srcH), 1.0);

        var longEdge = Math.Max(srcW, srcH);
        var scaledLongEdge = longEdge * scale;
        if (scaledLongEdge > 4096)
            scale *= 4096.0 / scaledLongEdge;

        var fitW = Math.Max(1, (int)Math.Round(srcW * scale));
        var fitH = Math.Max(1, (int)Math.Round(srcH * scale));
        return (fitW, fitH);
    }

    /// <summary>
    /// COPIED from <c>StillRenderer.FitLongEdge</c>: fits <paramref name="width"/> x
    /// <paramref name="height"/> inside a SQUARE box of <paramref name="target"/>, putting the
    /// long edge exactly on the target and never enlarging. The server uses this because its API
    /// hands out a single target number (a square thumbnail box); this part instead always has a
    /// full paneW x paneH and reaches its fit numbers directly from <see cref="Fit"/>, so nothing
    /// in the main decode path (<c>StillDecoder</c>) calls this. It is copied and tested anyway
    /// because plan section 6 asks for parity with the server's own test cases.
    /// </summary>
    public static (int Width, int Height) FitLongEdge(int width, int height, int target)
    {
        if (width <= 0 || height <= 0)
            return (Math.Max(1, width), Math.Max(1, height));

        var longEdge = Math.Max(width, height);
        if (target >= longEdge)
            return (width, height);

        return width >= height
            ? (target, Math.Max(1, (int)Math.Round(height * (double)target / width)))
            : (Math.Max(1, (int)Math.Round(width * (double)target / height)), target);
    }

    /// <summary>
    /// COPIED from <c>StillRenderer.ChooseScaledDimensions</c>. Asks the codec for a decode size
    /// at or above <paramref name="target"/> on the long edge, walking the scale upward until the
    /// candidate covers it, so the step that follows is always a downscale and never an
    /// interpolation upward. Plan section 3.1 step 4.
    /// </summary>
    public static SKSizeI ChooseScaledDimensions(SKCodec codec, int target)
    {
        var source = codec.Info;
        var longEdge = Math.Max(source.Width, source.Height);
        var full = new SKSizeI(source.Width, source.Height);

        if (target >= longEdge)
            return full;

        var scale = target / (float)longEdge;
        for (var attempt = 0; attempt < 8 && scale <= 1f; attempt++)
        {
            var candidate = codec.GetScaledDimensions(scale);

            if (candidate.Width > 0 && candidate.Height > 0 &&
                Math.Max(candidate.Width, candidate.Height) >= target)
            {
                return candidate;
            }

            scale *= 2f;
        }

        return full;
    }

    /// <summary>
    /// COPIED from <c>StillRenderer.OrientationMatrix</c>. The eight EXIF orientations as
    /// transforms from stored space into displayed space. Values 1-8 of the TIFF
    /// <c>Orientation</c> tag, which <see cref="SKCodec.EncodedOrigin"/> mirrors exactly.
    /// </summary>
    public static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        // 2: mirrored horizontally.
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        // 3: rotated 180 degrees.
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        // 4: mirrored vertically.
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        // 5: transposed about the leading diagonal.
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        // 6: rotated 90 degrees clockwise -- by far the commonest, a phone held upright.
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        // 7: transposed about the trailing diagonal.
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        // 8: rotated 90 degrees anticlockwise.
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <summary>COPIED from <c>StillRenderer.SwapsAxes</c>. EXIF orientations 5-8 are the transposed ones; they swap width and height.</summary>
    public static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
               or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Plan section 3.1, "Reusing a frame after the pane changed size": a cached frame at
    /// <paramref name="frameWidth"/> x <paramref name="frameHeight"/>, decoded from a source whose
    /// displayed size (after orientation) is <paramref name="sourceWidth"/> x
    /// <paramref name="sourceHeight"/>, is still usable for a pane of
    /// <paramref name="paneW"/> x <paramref name="paneH"/> if the frame is already at source size
    /// (nothing bigger will ever be delivered -- this part never upscales) or if the frame's long
    /// edge is at least the new pane's target long edge.
    /// </summary>
    public static bool FrameCovers(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight, int paneW, int paneH)
    {
        if (frameWidth == sourceWidth && frameHeight == sourceHeight)
            return true;

        var (fitW, fitH) = Fit(sourceWidth, sourceHeight, paneW, paneH);
        var target = Math.Max(fitW, fitH);
        var frameLongEdge = Math.Max(frameWidth, frameHeight);
        return frameLongEdge >= target;
    }
}
