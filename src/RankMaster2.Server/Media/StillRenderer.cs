using Microsoft.Extensions.Options;
using SkiaSharp;

namespace RankMaster2.Server.Media;

/// <summary>Thrown when a file exists but is not a decodable image → <c>422 media_decode_failed</c>.</summary>
public sealed class StillDecodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Pixel dimensions of a still <b>after</b> EXIF orientation (§ 12.1).</summary>
public readonly record struct StillDimensions(int Width, int Height);

/// <summary>
/// Decodes and re-encodes stills with SkiaSharp, and this is where the server's memory budget is
/// won or lost.
/// <para/>
/// <b>The whole point of this class.</b> Decoding a 48 MP JPEG in full and then shrinking it needs
/// a single 8000 × 6000 pixel buffer — 144 MB at three bytes a pixel, 192 MB at four — and a
/// measured ~190 MB of process working set. Decoding it <i>at the target size</i> costs a fraction
/// of that. The server's entire budget is ~100-150 MB, so one careless decode spends all of it.
/// Nothing in here may ever load an image and then resize it.
/// <para/>
/// Skia's reduced-size decode is <see cref="SKCodec.GetScaledDimensions"/>: the codec is asked
/// what sizes it can produce natively, and the pixels are decoded straight into one of them. For
/// JPEG those are the same power-of-two IDCT steps ImageSharp used, so the shape of the saving is
/// unchanged. <see cref="ChooseScaledDimensions"/> never picks a size below the target, so the
/// step that follows is always a downscale and never an interpolation upward.
/// <para/>
/// The target box is square (<c>w × w</c>), for two reasons. It is what § 12.3 asks for — "if the
/// source's <b>long edge</b> after orientation is below the requested width, the server serves it
/// at source size" — and it is orientation-proof: the decode happens in stored orientation, before
/// EXIF rotation, and a square box is the one shape that means the same thing either way round.
/// <para/>
/// Every pixel buffer comes from <see cref="DecodeBudget"/> rather than from Skia, which is what
/// makes the ceiling real and the memory test deterministic. See that class for why.
/// </summary>
public sealed class StillRenderer : IDisposable
{
    /// <summary>
    /// Quality for the downscale. Mitchell is the standard photographic choice: sharper than a
    /// plain triangle filter, without the ringing Catmull-Rom puts on high-contrast edges.
    /// </summary>
    private static readonly SKSamplingOptions Downscale = new(SKCubicResampler.Mitchell);

    /// <summary>A rotate or flip moves whole pixels, so it needs no resampling at all.</summary>
    private static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private readonly MediaOptions _options;
    private readonly SemaphoreSlim _decodeSlots;
    private readonly DecodeBudget _budget;

    public StillRenderer(IOptions<MediaOptions> options)
        : this(options.Value)
    {
    }

    public StillRenderer(MediaOptions options)
    {
        _options = options;
        _decodeSlots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentDecodes));
        _budget = new DecodeBudget(Math.Max(16, options.DecodeMemoryLimitMegabytes) * 1024L * 1024L);
    }

    /// <summary>The ceiling every decode runs under. Tests measure against this.</summary>
    public DecodeBudget Budget => _budget;

    /// <summary>
    /// A header read, not a decode (§ 12.1: "Reading <c>width</c>/<c>height</c> for a still is a
    /// header read, not a full decode"). The dimensions come back with EXIF orientation already
    /// applied, because that is what <c>MediaMeta</c> promises. No pixel memory is taken.
    /// </summary>
    public StillDimensions Identify(string path)
    {
        using var stream = OpenRead(path);
        using var codec = CreateCodec(stream, path);

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            throw new StillDecodeException("Not a decodable image: " + Path.GetFileName(path));

        return SwapsAxes(codec.EncodedOrigin)
            ? new StillDimensions(info.Height, info.Width)
            : new StillDimensions(info.Width, info.Height);
    }

    /// <summary>
    /// Renders one variant into <paramref name="destination"/>.
    /// <para/>
    /// EXIF orientation is applied, the embedded ICC profile is applied and the result is sRGB,
    /// aspect ratio is preserved and the image is never cropped and <b>never upscaled</b> — all
    /// § 12.3.
    /// </summary>
    public async Task RenderAsync(string path, StillVariant variant, Stream destination, CancellationToken cancellationToken)
    {
        await _decodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Render(path, variant, destination);
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    private void Render(string path, StillVariant variant, Stream destination)
    {
        using var stream = OpenRead(path);
        using var codec = CreateCodec(stream, path);

        var source = codec.Info;
        if (source.Width <= 0 || source.Height <= 0)
            throw new StillDecodeException("Not a decodable image: " + Path.GetFileName(path));

        var origin = codec.EncodedOrigin;
        var swap = SwapsAxes(origin);

        // "Never upscale" is decided from the displayed long edge, before a pixel is allocated.
        var displayedLongEdge = Math.Max(source.Width, source.Height);
        var target = Math.Min(variant.TargetWidth, displayedLongEdge);

        // The line this whole class exists for: decode straight into a size the codec can produce.
        var scaled = ChooseScaledDimensions(codec, target);

        // Asking for an sRGB destination is what applies the embedded profile: Skia converts from
        // codec.Info.ColorSpace to this one during the decode, so an Adobe RGB or Display P3
        // photograph arrives already converted rather than arriving oversaturated (§ 12.3).
        using var srgb = SKColorSpace.CreateSrgb();
        var alphaType = source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, alphaType, srgb);

        // Fit the decoded size into the square target box. Computing the long edge as exactly the
        // target, rather than scaling both axes by a ratio, keeps the delivered long edge on the
        // number the client asked for instead of a pixel either side of it.
        var (resizedWidth, resizedHeight) = FitLongEdge(scaled.Width, scaled.Height, target);

        var outputWidth = swap ? resizedHeight : resizedWidth;
        var outputHeight = swap ? resizedWidth : resizedHeight;

        using var finished = Decode(codec, decodeInfo, resizedWidth, resizedHeight, origin, outputWidth, outputHeight, path);
        Encode(finished, variant, destination);
    }

    /// <summary>
    /// Decode, downscale and orient, holding as little at once as the steps allow: the decode
    /// buffer is released before the orientation buffer is taken.
    /// </summary>
    private RenderedStill Decode(
        SKCodec codec,
        SKImageInfo decodeInfo,
        int resizedWidth,
        int resizedHeight,
        SKEncodedOrigin origin,
        int outputWidth,
        int outputHeight,
        string path)
    {
        RenderedStill? resized = null;
        var decodeBuffer = Reserve(decodeInfo.BytesSize64, path);

        try
        {
            using var decoded = new SKBitmap();
            if (!decoded.InstallPixels(decodeInfo, decodeBuffer.Pointer, decodeInfo.RowBytes))
                throw new StillDecodeException("Could not install a decode buffer for " + Path.GetFileName(path));

            var result = codec.GetPixels(decodeInfo, decodeBuffer.Pointer);

            // IncompleteInput means the scan data ran out part way — a half-copied photograph.
            // Skia has still filled what it read, and § 12.1's contract test allows either serving
            // that or refusing it, so the partial image is served rather than thrown away.
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new StillDecodeException($"Not a decodable image: {Path.GetFileName(path)} ({result}).");

            if (resizedWidth == decodeInfo.Width && resizedHeight == decodeInfo.Height)
            {
                resized = new RenderedStill(decoded, decodeBuffer);
                decodeBuffer = null;
            }
            else
            {
                var resizedInfo = decodeInfo.WithSize(resizedWidth, resizedHeight);
                resized = Allocate(resizedInfo, path);

                if (!decoded.ScalePixels(resized.Bitmap.PeekPixels(), Downscale))
                    throw new StillDecodeException("Could not resample " + Path.GetFileName(path));
            }
        }
        finally
        {
            // Released here rather than at the end: the full-size decode buffer is the big one,
            // and it must not still be held while the oriented copy is taken.
            decodeBuffer?.Dispose();
        }

        if (origin is SKEncodedOrigin.TopLeft)
            return resized;

        try
        {
            return Orient(resized, origin, outputWidth, outputHeight, path);
        }
        finally
        {
            resized.Dispose();
        }
    }

    /// <summary>
    /// Applies EXIF orientation. Skia reports it on <see cref="SKCodec.EncodedOrigin"/> but never
    /// applies it during decode — unlike ImageSharp's <c>AutoOrient</c>, this step is the caller's
    /// to do, and forgetting it serves every phone photograph on its side.
    /// </summary>
    private RenderedStill Orient(RenderedStill source, SKEncodedOrigin origin, int outputWidth, int outputHeight, string path)
    {
        var info = source.Bitmap.Info.WithSize(outputWidth, outputHeight);
        var oriented = Allocate(info, path);

        try
        {
            using var surface = SKSurface.Create(info, oriented.Buffer.Pointer, info.RowBytes);
            if (surface is null)
                throw new StillDecodeException("Could not create an orientation surface for " + Path.GetFileName(path));

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var matrix = OrientationMatrix(origin, source.Bitmap.Width, source.Bitmap.Height);
            canvas.SetMatrix(matrix);

            // Sharing rather than copying the pixels: SKImage.FromBitmap copies a mutable bitmap
            // and aliases an immutable one, and a needless copy of the decoded image is exactly
            // the kind of allocation this class exists to avoid.
            source.Bitmap.SetImmutable();
            using var image = SKImage.FromBitmap(source.Bitmap);
            canvas.DrawImage(image, 0, 0, Exact, paint: null);
            canvas.Flush();

            return oriented;
        }
        catch
        {
            oriented.Dispose();
            throw;
        }
    }

    private void Encode(RenderedStill still, StillVariant variant, Stream destination)
    {
        using var pixels = still.Bitmap.PeekPixels();

        var ok = variant.Format == StillFormat.Jpeg
            ? pixels.Encode(destination, new SKJpegEncoderOptions(_options.JpegQuality))
            : pixels.Encode(destination, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, _options.WebpQuality));

        if (!ok)
            throw new StillDecodeException($"Could not encode the still as {variant.Format}.");
    }

    /// <summary>
    /// Asks the codec for a decode size at or above <paramref name="target"/> on the long edge.
    /// <para/>
    /// <see cref="SKCodec.GetScaledDimensions"/> returns a size the codec can actually produce,
    /// which for JPEG is the source over 1, 2, 4 or 8, and for a format with no sampled decode is
    /// simply the source. It is not guaranteed to round upward, so the result is checked and the
    /// scale walked back toward 1 until the long edge covers the target. Decoding below the target
    /// and scaling up afterwards would be an upscale, which § 12.3 forbids, and would also lose
    /// detail the file actually contains.
    /// </summary>
    private static SKSizeI ChooseScaledDimensions(SKCodec codec, int target)
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
    /// Fits <paramref name="width"/> × <paramref name="height"/> inside a square box of
    /// <paramref name="target"/>, putting the long edge exactly on the target and never enlarging.
    /// </summary>
    internal static (int Width, int Height) FitLongEdge(int width, int height, int target)
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
    /// The eight EXIF orientations as transforms from stored space into displayed space. Values
    /// 1-8 of the TIFF <c>Orientation</c> tag, which <see cref="SKEncodedOrigin"/> mirrors exactly.
    /// </summary>
    internal static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        // 2: mirrored horizontally.
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        // 3: rotated 180°.
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        // 4: mirrored vertically.
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        // 5: transposed about the leading diagonal.
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        // 6: rotated 90° clockwise — by far the commonest, a phone held upright.
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        // 7: transposed about the trailing diagonal.
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        // 8: rotated 90° anticlockwise.
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <summary>EXIF orientations 5-8 are the transposed ones; they swap width and height.</summary>
    internal static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
               or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    private RenderedStill Allocate(SKImageInfo info, string path)
    {
        var buffer = Reserve(info.BytesSize64, path);
        try
        {
            var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, buffer.Pointer, info.RowBytes))
            {
                bitmap.Dispose();
                throw new StillDecodeException("Could not install a pixel buffer for " + Path.GetFileName(path));
            }

            return new RenderedStill(bitmap, buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes memory from the budget, turning a refusal into the 422 the contract asks for rather
    /// than letting it escape as a 500.
    /// </summary>
    private BudgetedBuffer Reserve(long bytes, string path)
    {
        try
        {
            return _budget.Allocate(bytes);
        }
        catch (DecodeBudgetExceededException ex)
        {
            throw new StillDecodeException(
                $"{Path.GetFileName(path)} is too large to decode inside the server's memory budget.", ex);
        }
    }

    /// <summary>
    /// Opens a codec, translating every way Skia can decline into the one exception this layer
    /// reports as <c>422 media_decode_failed</c>.
    /// <para/>
    /// Skia is markedly better behaved here than ImageSharp 4.1.1, which threw a bare
    /// <see cref="NullReferenceException"/> from inside its JPEG decoder on a file with a valid
    /// header and noise for scan data. Skia returns <see cref="SKCodecResult"/> or a null codec
    /// instead. The catch-all stays anyway: the bytes come off the user's disk and a decoder fed
    /// arbitrary input may fail in any way it likes, and none of those ways is a server fault.
    /// </summary>
    private static SKCodec CreateCodec(Stream stream, string path)
    {
        SKCodec? codec;
        SKCodecResult result;

        try
        {
            codec = SKCodec.Create(stream, out result);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new StillDecodeException("Not a decodable image: " + Path.GetFileName(path), ex);
        }

        if (codec is null)
            throw new StillDecodeException($"Not a decodable image: {Path.GetFileName(path)} ({result}).");

        return codec;
    }

    /// <summary>
    /// Whether an exception from a decode means "this file is not a decodable image", which is
    /// <c>422 media_decode_failed</c>, rather than something the caller should see as a 500.
    /// Cancellation and a file that has gone missing under the read are the two things that are
    /// genuinely not decode failures, so they pass through to their own handling.
    /// </summary>
    private static bool IsDecodeFailure(Exception ex) =>
        ex is not OperationCanceledException
        and not StillDecodeException
        and not DecodeBudgetExceededException
        and not FileNotFoundException
        and not DirectoryNotFoundException
        and not UnauthorizedAccessException
        and not OutOfMemoryException
        and not StackOverflowException;

    /// <summary>
    /// Sequential, and shared for write and delete: on Windows a plain read handle blocks the move
    /// behind a discard, and a decode must never be the reason an action fails. The handle lives
    /// only as long as the codec reads through it.
    /// </summary>
    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 64 * 1024,
        });

    public void Dispose() => _decodeSlots.Dispose();

    /// <summary>A bitmap and the budgeted buffer its pixels live in; the two die together.</summary>
    private sealed class RenderedStill(SKBitmap bitmap, BudgetedBuffer buffer) : IDisposable
    {
        public SKBitmap Bitmap { get; } = bitmap;

        public BudgetedBuffer Buffer { get; } = buffer;

        public void Dispose()
        {
            Bitmap.Dispose();
            Buffer.Dispose();
        }
    }
}
