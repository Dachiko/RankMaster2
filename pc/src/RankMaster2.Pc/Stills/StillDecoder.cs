using SkiaSharp;

namespace RankMaster2.Pc.Stills;

/// <summary>Either a decoded <see cref="StillFrame"/> or a mapped <see cref="StillFailure"/> with detail. Plan section 4.</summary>
internal readonly struct DecodeResult
{
    public StillFrame? Frame { get; }
    public StillFailure Failure { get; }
    public string? Detail { get; }

    private DecodeResult(StillFrame? frame, StillFailure failure, string? detail)
    {
        Frame = frame;
        Failure = failure;
        Detail = detail;
    }

    public static DecodeResult Ok(StillFrame frame) => new(frame, default, null);
    public static DecodeResult Fail(StillFailure failure, string detail) => new(null, failure, detail);

    public bool IsSuccess => Frame is not null;
}

/// <summary>
/// The seam StillSource decodes through, so ordering/eviction/memory can be tested with a
/// FakeDecoder and no disk (plan section 4).
/// </summary>
internal interface IStillDecoder
{
    DecodeResult Decode(string path, int paneW, int paneH);
}

/// <summary>Signals "not a decodable image" without carrying a Skia-specific type past this file.</summary>
internal sealed class NotAnImageException(string detail) : Exception(detail);

/// <summary>
/// path + pane + budget -&gt; <see cref="StillFrame"/> or <see cref="StillFailure"/>. Plan section
/// 3.1 steps 1-7 (the decode-at-size rule) and section 3.7 (the failure mapping).
/// <para/>
/// The decode technique is <c>StillRenderer</c>'s (src/RankMaster2.Server/Media/StillRenderer.cs),
/// copied and adapted: the server fits into a single square target number because its HTTP API
/// hands out one size; this part always has a full <c>paneW x paneH</c> box, so
/// <see cref="DecodeGeometry.Fit"/> replaces the server's per-call square fit. The pixel format is
/// fixed to BGRA8888 premultiplied sRGB for every frame (plan section 1), where the server keeps
/// an opaque/premultiplied branch for its own encode step -- there is no encode here, so there is
/// no reason for the branch.
/// </summary>
internal sealed class StillDecoder : IStillDecoder
{
    /// <summary>Mitchell: sharper than a triangle filter, without Catmull-Rom's ringing on high-contrast edges. Same choice as the server, same reason.</summary>
    private static readonly SKSamplingOptions Downscale = new(SKCubicResampler.Mitchell);

    /// <summary>A rotate or flip moves whole pixels; it needs no resampling at all.</summary>
    private static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private readonly DecodeBudget _budget;
    private readonly DecodeRoom _room;

    public StillDecoder(DecodeBudget budget) : this(budget, new DecodeRoom(budget))
    {
    }

    internal StillDecoder(DecodeBudget budget, DecodeRoom room)
    {
        _budget = budget;
        _room = room;
    }

    /// <summary>The ceiling every decode runs under. Tests measure against this.</summary>
    public DecodeBudget Budget => _budget;

    /// <summary>The admission gate the two decode workers share (plan § 3.3 / AUDIT2.md § 2.1).</summary>
    internal DecodeRoom Room => _room;

    public DecodeResult Decode(string path, int paneW, int paneH)
    {
        try
        {
            using var stream = OpenRead(path);
            using var codec = CreateCodec(stream, path);
            return DecodeWithCodec(path, codec, paneW, paneH);
        }
        catch (TooLargeException ex)
        {
            return DecodeResult.Fail(StillFailure.TooLarge, ex.Message);
        }
        catch (NotAnImageException ex)
        {
            return DecodeResult.Fail(StillFailure.NotAnImage, ex.Message);
        }
        catch (DecodeBudgetExceededException)
        {
            // Defensive only: DecodeWithCodec catches its own budget exceptions locally so the
            // detail message can name the source dimensions (plan section 6, Too_large test).
            return DecodeResult.Fail(StillFailure.TooLarge, $"{Path.GetFileName(path)} is too large to show inside the memory budget.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DecodeResult.Fail(StillFailure.Missing, $"{Path.GetFileName(path)}: file is gone.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return DecodeResult.Fail(StillFailure.Unreadable, $"{Path.GetFileName(path)}: cannot be read ({ex.Message}).");
        }
        catch (IOException ex)
        {
            return DecodeResult.Fail(StillFailure.Unreadable, $"{Path.GetFileName(path)}: cannot be read ({ex.Message}).");
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            return DecodeResult.Fail(StillFailure.NotAnImage, $"{Path.GetFileName(path)} is not a valid image ({ex.GetType().Name}: {ex.Message}).");
        }
        // OperationCanceledException, OutOfMemoryException, StackOverflowException: not this
        // file's fault (plan section 3.7's exception filter, IsDecodeFailure) -- propagate.
    }

    private DecodeResult DecodeWithCodec(string path, SKCodec codec, int paneW, int paneH)
    {
        var id = Path.GetFileName(path);
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            throw new NotAnImageException($"{id} is not a valid image (0 x 0).");

        var origin = codec.EncodedOrigin;
        var swap = DecodeGeometry.SwapsAxes(origin);

        // Step 1: displayed source size.
        var srcW = swap ? info.Height : info.Width;
        var srcH = swap ? info.Width : info.Height;

        // Step 2: fit to the pane, never upscale, cap the long edge at 4096.
        var (fitW, fitH) = DecodeGeometry.Fit(srcW, srcH, paneW, paneH);

        // The fit, back in STORED orientation -- what the decode buffer and the downscale target
        // are sized to, before step 7 turns it right-way-up.
        var storedFitW = swap ? fitH : fitW;
        var storedFitH = swap ? fitW : fitH;

        // Step 3: target long edge (orientation-proof -- the same number either way round).
        var target = Math.Max(fitW, fitH);

        // What the finished frame will cost, whichever way round it ends up.
        var frameBytes = (long)fitW * fitH * 4;

        // Step 4: ask the codec for a decode size at or above the target.
        var scaled = DecodeGeometry.ChooseScaledDimensions(codec, target);

        using var srgb = SKColorSpace.CreateSrgb();

        // Step 5: decode. BGRA8888 premultiplied into an sRGB destination -- asking for sRGB is
        // what applies the embedded ICC profile during the decode (plan section 3.2).
        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);

        BudgetedBuffer? rawBuffer;
        SKBitmap? rawBitmap;
        bool isPartial;
        RawOutcome outcome;
        string? failDetail;

        // Plan § 3.3 / AUDIT2.md § 2.1: claim this decode's whole worst-case peak before allocating
        // any of it, waiting for the other worker's decode if the two would not fit at once. Taken
        // while holding nothing, so a wait here can never be half of a deadlock; released in the
        // finally below, whatever happens.
        var reservation = Reserve(id, info, Peak(decodeInfo, storedFitW, storedFitH, frameBytes, origin));
        try
        {
            try
            {
                outcome = DecodeRaw(codec, decodeInfo, id, out rawBuffer, out rawBitmap, out isPartial, out failDetail);
            }
            catch (DecodeBudgetExceededException)
            {
                throw TooLarge(id, info);
            }

            if (outcome == RawOutcome.InvalidScale)
            {
                // The codec lied about being able to produce `scaled` (measured: PNG/BMP always do
                // this -- they only ever decode at source size). Retry once at full stored size --
                // which needs more room, so the reservation is re-taken at the bigger figure. Nothing
                // of this decode's is allocated at this point (DecodeRaw frees its buffer before
                // reporting InvalidScale), so giving the smaller reservation back and queueing again
                // for a larger one is as safe as the first claim was.
                decodeInfo = decodeInfo.WithSize(info.Width, info.Height);
                reservation.Dispose();
                reservation = Reserve(id, info, Peak(decodeInfo, storedFitW, storedFitH, frameBytes, origin));
                try
                {
                    outcome = DecodeRaw(codec, decodeInfo, id, out rawBuffer, out rawBitmap, out isPartial, out failDetail);
                }
                catch (DecodeBudgetExceededException)
                {
                    throw TooLarge(id, info);
                }
            }

            if (outcome == RawOutcome.Failed)
                throw new NotAnImageException(failDetail!);

            // Step 6: downscale to the stored-orientation fit, if the decode came out bigger.
            BudgetedBuffer fitBuffer;
            SKBitmap fitBitmap;
            if (decodeInfo.Width == storedFitW && decodeInfo.Height == storedFitH)
            {
                fitBuffer = rawBuffer!;
                fitBitmap = rawBitmap!;
            }
            else
            {
                var fitInfo = new SKImageInfo(storedFitW, storedFitH, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
                try
                {
                    fitBuffer = _budget.Allocate(fitInfo.BytesSize64);
                }
                catch (DecodeBudgetExceededException)
                {
                    rawBitmap!.Dispose();
                    rawBuffer!.Dispose();
                    throw TooLarge(id, info);
                }

                fitBitmap = new SKBitmap();
                try
                {
                    if (!fitBitmap.InstallPixels(fitInfo, fitBuffer.Pointer, fitInfo.RowBytes))
                        throw new NotAnImageException($"{id}: could not install a resample buffer.");

                    using var destination = fitBitmap.PeekPixels();
                    if (!rawBitmap!.ScalePixels(destination, Downscale))
                        throw new NotAnImageException($"{id}: could not resample.");
                }
                catch
                {
                    fitBitmap.Dispose();
                    fitBuffer.Dispose();
                    throw;
                }
                finally
                {
                    // The decode buffer is the big one; free it before the orientation buffer (if
                    // any) is taken, so peak memory is decodeBuffer+fitBuffer, then fitBuffer+
                    // orientedBuffer, never all three at once (plan section 3.1, final paragraph).
                    rawBitmap!.Dispose();
                    rawBuffer!.Dispose();
                }
            }

            // Step 7: orient.
            if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            {
                fitBitmap.Dispose(); // the SKBitmap wrapper only; fitBuffer's native memory is the frame's
                return DecodeResult.Ok(new StillFrame(id, fitBuffer, fitW, fitH, srcW, srcH, isPartial));
            }

            var orientedInfo = new SKImageInfo(fitW, fitH, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
            BudgetedBuffer orientedBuffer;
            try
            {
                orientedBuffer = _budget.Allocate(orientedInfo.BytesSize64);
            }
            catch (DecodeBudgetExceededException)
            {
                fitBitmap.Dispose();
                fitBuffer.Dispose();
                throw TooLarge(id, info);
            }

            try
            {
                using var surface = SKSurface.Create(orientedInfo, orientedBuffer.Pointer, orientedInfo.RowBytes)
                    ?? throw new NotAnImageException($"{id}: could not create an orientation surface.");

                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(DecodeGeometry.OrientationMatrix(origin, storedFitW, storedFitH));

                fitBitmap.SetImmutable();
                using var image = SKImage.FromBitmap(fitBitmap);
                canvas.DrawImage(image, 0, 0, Exact, paint: null);
                canvas.Flush();
            }
            catch
            {
                orientedBuffer.Dispose();
                throw;
            }
            finally
            {
                fitBitmap.Dispose();
                fitBuffer.Dispose();
            }

            return DecodeResult.Ok(new StillFrame(id, orientedBuffer, fitW, fitH, srcW, srcH, isPartial));
        }
        finally
        {
            reservation.Dispose();
        }
    }

    /// <summary>
    /// The most pixel memory this decode can hold at once, exactly: the decode buffer, plus the
    /// resample buffer when the decode does not land on the fit already (step 6 holds both), or
    /// twice the frame when the only extra buffer is the orientation surface (step 7 holds the fit
    /// and the oriented one together, and the decode buffer is freed before it).
    /// </summary>
    private static long Peak(SKImageInfo decodeInfo, int storedFitW, int storedFitH, long frameBytes, SKEncodedOrigin origin)
    {
        var rawBytes = decodeInfo.BytesSize64;
        var needsResample = decodeInfo.Width != storedFitW || decodeInfo.Height != storedFitH;
        if (needsResample)
            return rawBytes + frameBytes;

        var needsOrienting = origin is not (SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default);
        return needsOrienting ? rawBytes + frameBytes : rawBytes;
    }

    /// <summary>Reserves headroom, turning "there is no room and there will not be" into the same
    /// <see cref="TooLargeException"/> an oversized single file gets -- never into "not a valid
    /// image", which is what the owner must not be told about a photograph that is fine.</summary>
    private DecodeRoom.Reservation Reserve(string id, SKImageInfo info, long peakBytes)
    {
        try
        {
            return _room.Take(peakBytes);
        }
        catch (DecodeBudgetExceededException)
        {
            throw TooLarge(id, info);
        }
    }

    private static Exception TooLarge(string id, SKImageInfo sourceInfo) =>
        new TooLargeException($"{id} ({sourceInfo.Width} × {sourceInfo.Height}) is too large to show inside the memory budget.");

    /// <summary>Distinguishes a too-large decode from an ordinary not-an-image failure, both raised as exceptions so the deep call stack above can unwind through one finally-heavy method.</summary>
    private sealed class TooLargeException(string detail) : Exception(detail);

    private enum RawOutcome { Success, InvalidScale, Failed }

    /// <summary>One decode attempt at exactly <paramref name="decodeInfo"/>'s size. Frame 0 only (plan section 1: "Animated GIF: first frame only").</summary>
    private RawOutcome DecodeRaw(
        SKCodec codec,
        SKImageInfo decodeInfo,
        string id,
        out BudgetedBuffer? buffer,
        out SKBitmap? bitmap,
        out bool isPartial,
        out string? failDetail)
    {
        isPartial = false;
        failDetail = null;
        buffer = null;
        bitmap = null;

        // may throw DecodeBudgetExceededException; the caller adds source-dimension context to it
        var localBuffer = _budget.Allocate(decodeInfo.BytesSize64);
        var localBitmap = new SKBitmap();

        try
        {
            if (!localBitmap.InstallPixels(decodeInfo, localBuffer.Pointer, decodeInfo.RowBytes))
            {
                failDetail = $"{id}: could not install a decode buffer.";
                localBitmap.Dispose();
                localBuffer.Dispose();
                return RawOutcome.Failed;
            }

            var result = codec.GetPixels(decodeInfo, localBuffer.Pointer, new SKCodecOptions(0));

            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            {
                isPartial = result == SKCodecResult.IncompleteInput;
                buffer = localBuffer;
                bitmap = localBitmap;
                return RawOutcome.Success;
            }

            if (result == SKCodecResult.InvalidScale)
            {
                localBitmap.Dispose();
                localBuffer.Dispose();
                return RawOutcome.InvalidScale;
            }

            failDetail = $"{id} is not a valid image ({result}).";
            localBitmap.Dispose();
            localBuffer.Dispose();
            return RawOutcome.Failed;
        }
        catch
        {
            localBitmap.Dispose();
            localBuffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a codec, translating every way Skia can decline into <see cref="NotAnImageException"/>.
    /// COPIED in spirit from <c>StillRenderer.CreateCodec</c>.
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
            throw new NotAnImageException($"{Path.GetFileName(path)} is not a valid image.");
        }

        if (codec is null)
            throw new NotAnImageException($"{Path.GetFileName(path)} is not a valid image ({result}).");

        return codec;
    }

    /// <summary>
    /// COPIED from <c>StillRenderer.IsDecodeFailure</c>. Whether an exception means "this file is
    /// not a decodable image" rather than something that must propagate: cancellation,
    /// out-of-memory and a stack overflow are not the file's fault; not-found and permission
    /// problems are their own <see cref="StillFailure"/> values, handled by the caller's own
    /// catch clauses before this filter is reached.
    /// </summary>
    private static bool IsDecodeFailure(Exception ex) =>
        ex is not OperationCanceledException
        and not DecodeBudgetExceededException
        and not FileNotFoundException
        and not DirectoryNotFoundException
        and not UnauthorizedAccessException
        and not OutOfMemoryException
        and not StackOverflowException;

    /// <summary>
    /// COPIED from <c>StillRenderer.OpenRead</c>. Sequential, shared for write and delete: on
    /// Windows a plain read handle blocks a move behind a discard, and a decode must never be the
    /// reason an action fails. The handle lives only as long as one decode (plan section 1, "File
    /// handles").
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
}
