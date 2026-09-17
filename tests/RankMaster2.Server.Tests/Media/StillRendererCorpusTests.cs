using RankMaster2.Server.Media;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// The still renderer against <c>tests/corpus</c> — real camera EXIF, real ICC profiles, real
/// encoders — rather than against fixtures written from the same specifications the server reads.
/// <para/>
/// The generated fixtures and this corpus answer different questions. A generator emits what
/// someone thought to emit; a camera emits a progressive scan, a CMYK JPEG, an interlaced PNG and
/// an 8000 × 5000 image in 612 KB, and those are where a decoder swap actually shows.
/// <para/>
/// The corpus is git-ignored and built by <c>tests/corpus/build-corpus.sh</c>. T1c: every test
/// here is a <c>[SkippableFact]</c>/<c>[SkippableTheory]</c> and calls <see cref="Skip.If(bool, string)"/>
/// when the corpus (or one file of it) is absent, so a clone without ffmpeg still runs the suite —
/// and the run says, counted, that it skipped, rather than reporting a green result for nothing
/// exercised.
/// </summary>
public class StillRendererCorpusTests(ITestOutputHelper output)
{
    private static readonly string? Stills = Corpus.Directory("stills");
    private static readonly string? Broken = Corpus.Directory("broken");

    private static StillRenderer NewRenderer(int ceilingMegabytes = 128) =>
        new(new MediaOptions { DecodeMemoryLimitMegabytes = ceilingMegabytes, MaxConcurrentDecodes = 1 });

    private static SKBitmap Render(StillRenderer renderer, string path, int width, out long peakBytes)
    {
        renderer.Budget.ResetPeak();

        using var rendered = new MemoryStream();
        renderer.RenderAsync(
            path,
            new StillVariant(width, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None).GetAwaiter().GetResult();

        peakBytes = renderer.Budget.PeakBytes;
        return SKBitmap.Decode(rendered.ToArray());
    }

    /// <summary>
    /// The decompression bomb: 8000 × 5000 stored in 612 KB. A decoder that allocates from the
    /// header before anyone checks the dimensions takes 160 MB for it, which is more than the
    /// server's whole budget. Decoded at the target size it is a rounding error, and the ceiling
    /// is there to make the difference impossible to get wrong by accident.
    /// </summary>
    [SkippableFact]
    public void TheFortyMegapixelBombIsServedWithoutBlowingTheBudget()
    {
        Skip.If(Stills is null, Corpus.Missing);
        var path = Path.Combine(Stills!, "bomb_40mp.jpg");
        Skip.IfNot(File.Exists(path), "bomb_40mp.jpg not in the corpus");

        using var renderer = NewRenderer(ceilingMegabytes: 32);
        using var rendered = Render(renderer, path, 1080, out var peak);

        var onDisk = new FileInfo(path).Length;
        output.WriteLine($"bomb_40mp.jpg: {onDisk / 1024} KB on disk, rendered {rendered.Width}x{rendered.Height}, peak {peak / 1024.0 / 1024.0:F1} MB");

        Assert.Equal(1080, Math.Max(rendered.Width, rendered.Height));
        Assert.True(peak < 32L * 1024 * 1024, $"peak was {peak / 1024 / 1024} MB");
        Assert.Equal(0, renderer.Budget.LiveBytes);
    }

    /// <summary>
    /// All sixteen real EXIF orientation files, landscape and portrait. These come from a corpus
    /// built for exactly this purpose, and the numbers in the filenames are the ground truth: an
    /// orientation of 5-8 displays transposed, whatever the pixels are stored as.
    /// </summary>
    [SkippableTheory]
    [InlineData("landscape")]
    [InlineData("portrait")]
    public void RealExifOrientationsAllDisplayTheSameWayUp(string shape)
    {
        Skip.If(Stills is null, Corpus.Missing);

        using var renderer = NewRenderer();
        var seen = new List<(int Orientation, int Width, int Height)>();

        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var path = Path.Combine(Stills!, $"exif_{shape}_{orientation}.jpg");
            if (!File.Exists(path))
                continue;

            var identified = renderer.Identify(path);
            using var rendered = Render(renderer, path, 720, out _);

            output.WriteLine($"exif_{shape}_{orientation}: meta {identified.Width}x{identified.Height}, pixels {rendered.Width}x{rendered.Height}");

            // What meta promises and what still delivers must be the same picture. The still is
            // downscaled to the requested width, so the check is that they agree on shape rather
            // than on size: same way up, same proportions.
            Assert.Equal(
                identified.Width >= identified.Height,
                rendered.Width >= rendered.Height);

            Assert.True(
                Math.Abs(identified.Width / (double)identified.Height - rendered.Width / (double)rendered.Height) < 0.02,
                $"exif_{shape}_{orientation}: meta says {identified.Width}x{identified.Height} but the pixels are " +
                $"{rendered.Width}x{rendered.Height}, which is a different shape.");

            seen.Add((orientation, rendered.Width, rendered.Height));
        }

        Skip.If(seen.Count == 0, $"no exif_{shape}_* files in the corpus");

        // The whole point of the orientation tag: all eight encode the same picture, so all eight
        // must come out at the same displayed size. A renderer that ignores the tag gives the
        // transposed ones back the other way round.
        var distinct = seen.Select(s => (s.Width, s.Height)).Distinct().ToArray();
        Assert.True(
            distinct.Length == 1,
            $"The eight {shape} orientations encode one picture but rendered at {distinct.Length} different sizes: " +
            string.Join(", ", seen.Select(s => $"{s.Orientation}->{s.Width}x{s.Height}")));
    }

    /// <summary>
    /// Encodings a generator would not think to emit. Each one only has to come out as a plausible
    /// image at the requested size; the point is that none of them throws, hangs or returns
    /// something that is not an image.
    /// </summary>
    [SkippableTheory]
    [InlineData("progressive.jpg")]
    [InlineData("grayscale.jpg")]
    [InlineData("cmyk.jpg")]
    [InlineData("interlaced.png")]
    [InlineData("lossy.webp")]
    [InlineData("lossless.webp")]
    [InlineData("animated.gif")]
    [InlineData("tiny.png")]
    [InlineData("photo_cat.jpg")]
    [InlineData("photo_large.jpg")]
    [InlineData("wide_gamut.jpg")]
    public void EveryRealEncodingRendersToTheRequestedSize(string name)
    {
        Skip.If(Stills is null, Corpus.Missing);
        var path = Path.Combine(Stills!, name);
        Skip.IfNot(File.Exists(path), $"{name} not in the corpus");

        using var renderer = NewRenderer();
        var identified = renderer.Identify(path);
        using var rendered = Render(renderer, path, 720, out var peak);

        output.WriteLine($"{name}: source {identified.Width}x{identified.Height} -> {rendered.Width}x{rendered.Height}, peak {peak / 1024.0 / 1024.0:F1} MB");

        Assert.True(rendered.Width > 0 && rendered.Height > 0);

        // Never upscaled, and the long edge lands on the target whenever there was room to.
        var sourceLongEdge = Math.Max(identified.Width, identified.Height);
        var renderedLongEdge = Math.Max(rendered.Width, rendered.Height);
        Assert.Equal(Math.Min(720, sourceLongEdge), renderedLongEdge);

        // Aspect ratio survives, to within the rounding of a whole pixel.
        var sourceRatio = identified.Width / (double)identified.Height;
        var renderedRatio = rendered.Width / (double)rendered.Height;
        Assert.True(
            Math.Abs(sourceRatio - renderedRatio) < 0.02,
            $"{name}: aspect ratio moved from {sourceRatio:F3} to {renderedRatio:F3}.");

        Assert.Equal(0, renderer.Budget.LiveBytes);
    }

    /// <summary>
    /// The real wide-gamut photograph, not a synthesised one. Rendering it must change its pixels,
    /// because its numbers are not sRGB numbers.
    /// </summary>
    [SkippableFact]
    public void TheRealWideGamutPhotographIsConverted()
    {
        Skip.If(Stills is null, Corpus.Missing);
        var path = Path.Combine(Stills!, "wide_gamut.jpg");
        Skip.IfNot(File.Exists(path), "wide_gamut.jpg not in the corpus");

        using var renderer = NewRenderer();
        using var converted = Render(renderer, path, 360, out _);

        // The same file decoded with no destination profile: the stored numbers, untransformed.
        using var codec = SKCodec.Create(File.OpenRead(path))!;
        var raw = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque, colorspace: null);
        using var ignored = new SKBitmap();
        ignored.TryAllocPixels(raw);
        codec.GetPixels(raw, ignored.GetPixels());

        output.WriteLine($"wide_gamut.jpg embedded profile: {(codec.Info.ColorSpace is null ? "none parsed" : codec.Info.ColorSpace.IsSrgb ? "sRGB" : "wide gamut")}");

        Skip.If(codec.Info.ColorSpace is null, "Skia did not parse this profile; see the report on LUT-based profiles");
        Skip.If(codec.Info.ColorSpace!.IsSrgb, "the corpus file's profile is sRGB, so there is nothing to convert");

        var difference = MeanDifference(converted, ignored);
        output.WriteLine($"mean channel difference, converted vs stored numbers: {difference:F1}/255");

        Assert.True(
            difference > 1.5,
            $"SERVER_SPEC.md § 12.3: the profile is applied and the output is sRGB. The rendered pixels differ " +
            $"from the untransformed stored numbers by only {difference:F2}/255, so no conversion happened.");
    }

    /// <summary>
    /// The broken files. Every one is 422 territory, and not one of them may take the process with
    /// it — SPEC.md § Media policy: "Unreadable / corrupt files are skipped for that pair. The
    /// session does not crash."
    /// </summary>
    [SkippableTheory]
    [InlineData("empty.jpg")]
    [InlineData("header_only.jpg")]
    [InlineData("noise.jpg")]
    [InlineData("text_pretending.jpg")]
    [InlineData("truncated.jpg")]
    public void BrokenFilesAreRefusedCleanly(string name)
    {
        Skip.If(Broken is null, Corpus.Missing);
        var path = Path.Combine(Broken!, name);
        Skip.IfNot(File.Exists(path), $"{name} not in the corpus");

        using var renderer = NewRenderer();
        using var rendered = new MemoryStream();

        var thrown = Record.Exception(() => renderer.RenderAsync(
            path,
            new StillVariant(720, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None).GetAwaiter().GetResult());

        if (thrown is null)
        {
            // A partial decode is allowed — a truncated scan still carries the rows it read.
            output.WriteLine($"{name}: decoded what was there, {rendered.Length} bytes");
            Assert.True(rendered.Length > 0, $"{name} produced a 200 with an empty body, which is neither an image nor an error.");
        }
        else
        {
            output.WriteLine($"{name}: refused as {thrown.GetType().Name}");
            Assert.IsType<StillDecodeException>(thrown);
        }

        // Whatever happened, no memory leaked and the renderer is still usable.
        Assert.Equal(0, renderer.Budget.LiveBytes);
    }

    private static double MeanDifference(SKBitmap a, SKBitmap b)
    {
        const int steps = 10;
        double total = 0;
        var count = 0;

        for (var iy = 1; iy < steps; iy++)
        {
            for (var ix = 1; ix < steps; ix++)
            {
                var pa = a.GetPixel(a.Width * ix / steps, a.Height * iy / steps);
                var pb = b.GetPixel(b.Width * ix / steps, b.Height * iy / steps);
                total += Math.Abs(pa.Red - pb.Red) + Math.Abs(pa.Green - pb.Green) + Math.Abs(pa.Blue - pb.Blue);
                count += 3;
            }
        }

        return count == 0 ? 0 : total / count;
    }
}

/// <summary>Locates <c>tests/corpus/media</c>, which is git-ignored and may not be built.</summary>
internal static class Corpus
{
    /// <summary>The reason printed by <c>Skip.If</c> when the corpus was never built at all.</summary>
    public const string Missing = "corpus not built — run tests/corpus/build-corpus.sh";

    private static readonly Lazy<string?> Root = new(Find);

    public static string? Directory(string group)
    {
        if (Root.Value is null)
            return null;

        var path = Path.Combine(Root.Value, group);
        return System.IO.Directory.Exists(path) ? path : null;
    }

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "corpus", "media");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
