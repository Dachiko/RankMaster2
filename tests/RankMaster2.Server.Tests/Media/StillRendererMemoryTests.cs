using RankMaster2.Server.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// The memory budget, which is the one property of the media layer that cannot be inspected by
/// reading a response.
/// <para/>
/// <b>What is being measured, and why it is not <c>GC.GetTotalMemory</c>.</b> ImageSharp's pixel
/// buffers come from its own <see cref="MemoryAllocator"/>, which allocates unmanaged and pools.
/// The managed heap therefore barely moves whichever path you take — measured here at about 3 MB
/// either way — while the process working set moves from ~58 MB to ~189 MB. Sampling
/// <c>GC.GetTotalMemory</c> would give a test that passes on the careless path, which is worse
/// than no test.
/// <para/>
/// So the budget is enforced where the bytes actually are: the allocator is given a hard
/// accumulative ceiling, and the assertion is that the render completes under it. That is
/// deterministic — no sampling, no timing, no tolerance for a machine under load — and it fails
/// loudly the moment someone replaces the reduced-size decode with a decode-then-resize.
/// <see cref="FullDecodeOfTheSameImage_BlowsTheSameBudget"/> is the control that proves the
/// ceiling is a real constraint and not a number chosen to be un-hittable.
/// </summary>
public class StillRendererMemoryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Low tens of megabytes. A 48 MP JPEG decoded to 1080 px peaks at about 13 MB of allocator
    /// memory; this leaves generous headroom for a differently-shaped image and is still an order
    /// of magnitude below the 144 MB a single full-size RGB buffer needs.
    /// </summary>
    private const int BudgetMegabytes = 32;

    [Fact]
    public async Task Resizing_A48MegapixelJpeg_StaysInsideTheMemoryBudget()
    {
        var path = LargeImageFixture.Path;

        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = BudgetMegabytes,
            DecodePoolMegabytes = 0,
            MaxConcurrentDecodes = 1,
        });

        var before = SettledManagedBytes();
        var workingSetBefore = Environment.WorkingSet;

        using var rendered = new MemoryStream();
        await renderer.RenderAsync(
            path,
            new StillVariant(1080, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None);

        var managedDelta = SettledManagedBytes() - before;
        var workingSetDelta = Environment.WorkingSet - workingSetBefore;

        output.WriteLine($"source            {LargeImageFixture.Width}x{LargeImageFixture.Height} ({LargeImageFixture.Megapixels:F0} MP), {new FileInfo(path).Length / 1024 / 1024} MB on disk");
        output.WriteLine($"allocator ceiling {BudgetMegabytes} MB (hard, enforced)");
        output.WriteLine($"full decode needs {LargeImageFixture.FullDecodeBytes / 1024 / 1024} MB in one buffer");
        output.WriteLine($"managed delta     {managedDelta / 1024.0 / 1024.0:F1} MB");
        output.WriteLine($"working set delta {workingSetDelta / 1024.0 / 1024.0:F1} MB");
        output.WriteLine($"encoded           {rendered.Length / 1024} KB");

        // It completed, which under a hard accumulative ceiling means it never held more than
        // BudgetMegabytes of pixel buffers at once.
        Assert.True(rendered.Length > 0);

        rendered.Position = 0;
        using var check = Image.Load(rendered);
        Assert.Equal(1080, check.Width);
        Assert.Equal(810, check.Height);

        // The managed heap is the secondary signal. It is small on both paths, so this is a
        // sanity bound rather than the thing being proven.
        Assert.True(
            managedDelta < 24L * 1024 * 1024,
            $"Managed heap grew by {managedDelta / 1024 / 1024} MB, which is more than a target-size decode should cost.");
    }

    /// <summary>
    /// The control. Decoding the same file in full and then shrinking it needs one contiguous
    /// 8000 × 6000 × 3 = 144 MB buffer, so it cannot fit under the ceiling the renderer works
    /// inside. If this ever stops throwing, the ceiling has been raised to meaninglessness and the
    /// test above is no longer asserting anything.
    /// </summary>
    [Fact]
    public void FullDecodeOfTheSameImage_BlowsTheSameBudget()
    {
        var path = LargeImageFixture.Path;

        var configuration = Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AccumulativeAllocationLimitMegabytes = BudgetMegabytes,
            MaximumPoolSizeMegabytes = 0,
        });

        var thrown = Record.Exception(() =>
        {
            using var stream = File.OpenRead(path);

            // No TargetSize: the careless path, full decode and then shrink.
            using var image = Image.Load<Rgb24>(new DecoderOptions { Configuration = configuration }, stream);
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1080, 1080), Mode = ResizeMode.Max }));
            image.SaveAsJpeg(Stream.Null, new JpegEncoder { Quality = 85 });
        });

        Assert.NotNull(thrown);
        output.WriteLine("full decode under the same ceiling: " + thrown!.GetType().Name);

        // ImageSharp wraps the allocator's refusal in a decode failure; either is proof enough
        // that 144 MB did not fit where 13 MB did.
        var chain = Unwind(thrown).ToList();
        Assert.Contains(chain, e => e is InvalidMemoryOperationException);
    }

    /// <summary>
    /// Every allowed width, each against the ceiling it actually needs.
    /// <para/>
    /// The numbers are not uniform, and the reason is worth writing down: the JPEG decoder scales
    /// during the IDCT in powers of two, so the intermediate buffer is the source at 1/8, 1/4 or
    /// 1/2 depending on how far the target is below it. For this 8000 × 6000 source, widths up to
    /// 1080 land on the 1/4 step (2000 × 1500 × 3 = 9 MB) while 2160 lands on the 1/2 step
    /// (4000 × 3000 × 3 = 36 MB). So <c>w=2160</c> genuinely costs about four times <c>w=1080</c>
    /// — still a quarter of a full decode, and the reason the server's own default ceiling is
    /// 128 MB rather than the 32 MB the headline test uses.
    /// </summary>
    [Theory]
    [InlineData(360, BudgetMegabytes)]
    [InlineData(540, BudgetMegabytes)]
    [InlineData(720, BudgetMegabytes)]
    [InlineData(1080, BudgetMegabytes)]
    [InlineData(1440, 64)]
    [InlineData(2160, 64)]
    public async Task EveryAllowedWidth_RendersInsideItsBudget(int width, int budgetMegabytes)
    {
        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = budgetMegabytes,
            DecodePoolMegabytes = 0,
        });

        using var rendered = new MemoryStream();
        await renderer.RenderAsync(
            LargeImageFixture.Path,
            new StillVariant(width, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None);

        rendered.Position = 0;
        using var check = Image.Load(rendered);
        Assert.Equal(width, check.Width);
    }

    /// <summary>
    /// A thumbnail of the same 48 MP source. The smallest target is the one where a full decode
    /// would be most wasteful — and where it would be least visible in a response.
    /// </summary>
    [Fact]
    public async Task Thumbnailing_A48MegapixelJpeg_StaysInsideTheBudget()
    {
        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = BudgetMegabytes,
            DecodePoolMegabytes = 0,
        });

        using var rendered = new MemoryStream();
        await renderer.RenderAsync(
            LargeImageFixture.Path,
            new StillVariant(320, StillFormat.Jpeg, IsThumb: true, FormatSource.Default),
            rendered,
            CancellationToken.None);

        rendered.Position = 0;
        using var check = Image.Load(rendered);
        Assert.Equal(320, check.Width);
        Assert.Equal(240, check.Height);
    }

    private static long SettledManagedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static IEnumerable<Exception> Unwind(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            yield return e;
    }
}
