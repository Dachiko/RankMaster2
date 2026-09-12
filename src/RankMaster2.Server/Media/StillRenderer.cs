using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace RankMaster2.Server.Media;

/// <summary>Thrown when a file exists but is not a decodable image → <c>422 media_decode_failed</c>.</summary>
public sealed class StillDecodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Pixel dimensions of a still <b>after</b> EXIF orientation (§ 12.1).</summary>
public readonly record struct StillDimensions(int Width, int Height);

/// <summary>
/// Decodes and re-encodes stills, and this is where the server's memory budget is won or lost.
/// <para/>
/// <b>The whole point of this class.</b> Decoding a 48 MP JPEG in full and then shrinking it needs
/// a single 8000 × 6000 × 3 = 144 MB pixel buffer, and a measured ~190 MB of process working set.
/// Decoding it <i>at the target size</i> — ImageSharp's <see cref="DecoderOptions.TargetSize"/>,
/// which the JPEG decoder honours by scaling during the IDCT — peaks at about 13 MB of allocator
/// memory and ~58 MB of working set. The server's entire budget is ~100-150 MB, so one careless
/// decode spends all of it. Nothing in here may ever load an image and then resize it.
/// <para/>
/// The target box is square (<c>w × w</c>), for two reasons. It is what § 12.3 asks for — "if the
/// source's <b>long edge</b> after orientation is below the requested width, the server serves it
/// at source size" — and it is orientation-proof: <see cref="DecoderOptions.TargetSize"/> is
/// applied to the raw stored dimensions, before EXIF rotation, and a square box is the one shape
/// that means the same thing either way round.
/// </summary>
public sealed class StillRenderer : IDisposable
{
    private readonly MediaOptions _options;
    private readonly SemaphoreSlim _decodeSlots;
    private readonly Configuration _configuration;

    public StillRenderer(IOptions<MediaOptions> options)
        : this(options.Value)
    {
    }

    public StillRenderer(MediaOptions options)
    {
        _options = options;
        _decodeSlots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentDecodes));

        // A private allocator with a hard ceiling, so the budget is enforced rather than hoped
        // for. A decode that would exceed it throws instead of swelling the process.
        _configuration = Configuration.Default.Clone();
        _configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AccumulativeAllocationLimitMegabytes = Math.Max(16, options.DecodeMemoryLimitMegabytes),
            MaximumPoolSizeMegabytes = Math.Max(0, options.DecodePoolMegabytes),
        });
    }

    /// <summary>The ImageSharp configuration used for every decode. Tests measure against this.</summary>
    public Configuration Configuration => _configuration;

    /// <summary>
    /// A header read, not a decode (§ 12.1: "Reading <c>width</c>/<c>height</c> for a still is a
    /// header read, not a full decode"). The dimensions come back with EXIF orientation already
    /// applied, because that is what <c>MediaMeta</c> promises.
    /// </summary>
    public StillDimensions Identify(string path)
    {
        ImageInfo info;
        try
        {
            using var stream = OpenRead(path);
            info = Image.Identify(new DecoderOptions { Configuration = _configuration }, stream);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new StillDecodeException("Not a decodable image: " + Path.GetFileName(path), ex);
        }

        return SwapsAxes(OrientationOf(info.Metadata.ExifProfile))
            ? new StillDimensions(info.Height, info.Width)
            : new StillDimensions(info.Width, info.Height);
    }

    /// <summary>
    /// Renders one variant into <paramref name="destination"/>.
    /// <para/>
    /// EXIF orientation is applied (<c>AutoOrient</c>), the embedded ICC profile is applied and
    /// the result is sRGB (<see cref="ColorProfileHandling.Convert"/>), aspect ratio is preserved
    /// and the image is never cropped and <b>never upscaled</b> — all § 12.3.
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
        // Read the header first so the decoder can be told what to aim for, and so "never upscale"
        // is decided before a single pixel is allocated.
        var source = Identify(path);
        var longEdge = Math.Max(source.Width, source.Height);
        var target = Math.Min(variant.TargetWidth, longEdge);

        var decoderOptions = new DecoderOptions
        {
            Configuration = _configuration,

            // sRGB out, with any embedded profile applied to the pixels and then dropped (§ 12.3).
            ColorProfileHandling = ColorProfileHandling.Convert,

            // The line this whole class exists for. Square box: orientation-proof, long-edge
            // bounded, and never larger than the source, so the decoder cannot upscale.
            TargetSize = new Size(target, target),

            // An animation is served as its first frame; nothing here needs the rest of a GIF.
            MaxFrames = 1,
        };

        Image image;
        try
        {
            using var stream = OpenRead(path);
            image = Image.Load(decoderOptions, stream);
        }
        catch (InvalidMemoryOperationException ex)
        {
            throw new StillDecodeException("Image too large to decode inside the server's memory budget.", ex);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new StillDecodeException("Not a decodable image: " + Path.GetFileName(path), ex);
        }

        using (image)
        {
            // Orientation after decode: TargetSize works on the stored dimensions, so rotating
            // first would mean decoding first, which is the thing we are avoiding.
            image.Mutate(x => x.AutoOrient());

            // Formats with no reduced-size decode path (PNG, BMP, GIF) come back full size; this
            // is where they get fitted. For JPEG it is a no-op or a rounding-sized touch-up,
            // because the decoder already landed on or just above the target.
            if (Math.Max(image.Width, image.Height) > target)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(target, target),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Bicubic,
                }));
            }

            try
            {
                if (variant.Format == StillFormat.Jpeg)
                    image.SaveAsJpeg(destination, new JpegEncoder { Quality = _options.JpegQuality });
                else
                    image.SaveAsWebp(destination, new WebpEncoder { Quality = _options.WebpQuality, FileFormat = WebpFileFormatType.Lossy });
            }
            catch (InvalidMemoryOperationException ex)
            {
                throw new StillDecodeException("Image too large to encode inside the server's memory budget.", ex);
            }
        }
    }

    /// <summary>
    /// Whether an exception from a decode means "this file is not a decodable image", which is
    /// <c>422 media_decode_failed</c>, rather than something the caller should see as a 500.
    /// <para/>
    /// The net is deliberately wide, and measured rather than guessed: ImageSharp 4.1.1 throws a
    /// bare <see cref="NullReferenceException"/> from <c>JpegDecoderCore.ParseStream</c> for a file
    /// whose JFIF header parses and whose scan data is noise — the exact shape a half-copied photo
    /// takes. A decoder fed arbitrary bytes from the user's disk can fail in any way it likes, and
    /// none of those ways is a server fault worth a 500. Cancellation and a file that has gone
    /// missing under the read are the two things that are genuinely not decode failures, so they
    /// pass through to their own handling.
    /// </summary>
    private static bool IsDecodeFailure(Exception ex) =>
        ex is not OperationCanceledException
        and not StillDecodeException
        and not InvalidMemoryOperationException
        and not FileNotFoundException
        and not DirectoryNotFoundException
        and not UnauthorizedAccessException
        and not OutOfMemoryException
        and not StackOverflowException;

    /// <summary>
    /// Sequential, no share-write. The bytes are read once and the handle is closed before the
    /// image is used, mirroring SPEC.md § Pipeline's "decode from a disposed FileStream" so a
    /// discard or an undo is never blocked by a read that is still open.
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

    private static ushort OrientationOf(ExifProfile? exif)
    {
        if (exif is null)
            return 1;

        return exif.TryGetValue(ExifTag.Orientation, out var value) && value.Value is { } v ? v : (ushort)1;
    }

    /// <summary>EXIF orientations 5-8 are the transposed ones; they swap width and height.</summary>
    private static bool SwapsAxes(ushort orientation) => orientation is >= 5 and <= 8;

    public void Dispose() => _decodeSlots.Dispose();
}
