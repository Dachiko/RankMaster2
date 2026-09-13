using RankMaster2.Server.Media;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// The memory budget, which is the one property of the media layer that cannot be inspected by
/// reading a response.
/// <para/>
/// <b>What is being measured, and why it is not <c>GC.GetTotalMemory</c>.</b> Skia decodes into
/// native memory, so the managed heap barely moves whichever path the renderer takes — measured at
/// well under a megabyte either way, while the process working set moves by more than a hundred.
/// Sampling the managed heap would give a test that passes on the careless path, which is worse
/// than no test.
/// <para/>
/// So the renderer supplies Skia's pixel buffers itself, through <see cref="DecodeBudget"/>, and
/// these tests read that budget. Two things follow that the previous ImageSharp arrangement could
/// not do: the ceiling is enforced before the memory is taken, and
/// <see cref="DecodeBudget.PeakBytes"/> is the true high-water mark — so the assertion is the
/// actual number of bytes a resize cost, not merely that it finished.
/// <see cref="FullDecodeOfTheSameImage_BlowsTheSameBudget"/> is the control that proves the
/// ceiling is a real constraint and not a number chosen to be un-hittable.
/// </summary>
public class StillRendererMemoryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Low tens of megabytes. A 48 MP JPEG decoded to 1080 px peaks at about 12 MB; this leaves
    /// generous headroom for a differently-shaped image and is still an order of magnitude below
    /// the 192 MB a single full-size RGBA buffer needs.
    /// </summary>
    private const int BudgetMegabytes = 32;

    /// <summary>What one full-size RGBA buffer for the fixture would cost: 8000 × 6000 × 4.</summary>
    private const long FullDecodeBytes = (long)LargeImageFixture.Width * LargeImageFixture.Height * 4;

    [Fact]
    public async Task Resizing_A48MegapixelJpeg_StaysInsideTheMemoryBudget()
    {
        var path = LargeImageFixture.Path;

        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = BudgetMegabytes,
            MaxConcurrentDecodes = 1,
        });

        renderer.Budget.ResetPeak();
        var managedBefore = SettledManagedBytes();
        var workingSetBefore = Environment.WorkingSet;

        using var rendered = new MemoryStream();
        await renderer.RenderAsync(
            path,
            new StillVariant(1080, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None);

        var peak = renderer.Budget.PeakBytes;
        var managedDelta = SettledManagedBytes() - managedBefore;
        var workingSetDelta = Environment.WorkingSet - workingSetBefore;

        output.WriteLine($"source            {LargeImageFixture.Width}x{LargeImageFixture.Height} ({LargeImageFixture.Megapixels:F0} MP), {new FileInfo(path).Length / 1024 / 1024} MB on disk");
        output.WriteLine($"budget ceiling    {BudgetMegabytes} MB (hard, enforced before allocation)");
        output.WriteLine($"full decode needs {FullDecodeBytes / 1024 / 1024} MB in one buffer");
        output.WriteLine($"peak pixel bytes  {peak / 1024.0 / 1024.0:F1} MB   <-- the measurement");
        output.WriteLine($"managed delta     {managedDelta / 1024.0 / 1024.0:F1} MB");
        output.WriteLine($"working set delta {workingSetDelta / 1024.0 / 1024.0:F1} MB");
        output.WriteLine($"encoded           {rendered.Length / 1024} KB");

        Assert.True(rendered.Length > 0);

        // Every pixel buffer went back.
        Assert.Equal(0, renderer.Budget.LiveBytes);

        // The measurement itself: low tens of megabytes, not the 192 MB a full decode would need.
        Assert.True(
            peak < 24L * 1024 * 1024,
            $"Peak pixel memory was {peak / 1024 / 1024} MB; a target-size decode of this image should be nearer 12 MB.");

        Assert.True(
            peak < FullDecodeBytes / 4,
            $"Peak pixel memory was {peak:N0} bytes against {FullDecodeBytes:N0} for a full decode — not a reduced-size decode.");

        using var check = SKBitmap.Decode(rendered.ToArray());
        Assert.Equal(1080, check.Width);
        Assert.Equal(810, check.Height);

        Assert.True(
            managedDelta < 24L * 1024 * 1024,
            $"Managed heap grew by {managedDelta / 1024 / 1024} MB, which a native-buffer decode should not.");
    }

    /// <summary>
    /// The control. Decoding the same file at full size needs one 8000 × 6000 × 4 = 192 MB buffer,
    /// so it cannot fit under the ceiling the renderer works inside. If this ever stops throwing,
    /// the ceiling has been raised to meaninglessness and the test above asserts nothing.
    /// <para/>
    /// It goes through the same <see cref="DecodeBudget"/> the renderer uses, so what is being
    /// proven is that the budget refuses the careless path — not merely that Skia can be made to
    /// fail some other way.
    /// </summary>
    [Fact]
    public void FullDecodeOfTheSameImage_BlowsTheSameBudget()
    {
        var budget = new DecodeBudget(BudgetMegabytes * 1024L * 1024L);

        using var stream = File.OpenRead(LargeImageFixture.Path);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);

        // No GetScaledDimensions: the careless path, full decode and then shrink.
        var fullInfo = new SKImageInfo(
            codec!.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque,
            SKColorSpace.CreateSrgb());

        Assert.Equal(FullDecodeBytes, fullInfo.BytesSize64);

        var thrown = Assert.Throws<DecodeBudgetExceededException>(() => budget.Allocate(fullInfo.BytesSize64));

        output.WriteLine($"full decode of {codec.Info.Width}x{codec.Info.Height} wanted {thrown.RequestedBytes / 1024 / 1024} MB");
        output.WriteLine($"the ceiling the renderer runs under is {thrown.CeilingBytes / 1024 / 1024} MB");

        Assert.Equal(FullDecodeBytes, thrown.RequestedBytes);
        Assert.Equal(0, budget.LiveBytes);
    }

    /// <summary>
    /// And the same again through the renderer: told to produce the source's own size, it has
    /// nothing left to scale down to and is refused, which is what a ceiling being real looks
    /// like from the outside. A refusal is a 422, never a 500.
    /// </summary>
    [Fact]
    public async Task ARenderThatCannotFitTheBudget_IsRefusedAsUndecodable()
    {
        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = 16,
            MaxConcurrentDecodes = 1,
        });

        using var rendered = new MemoryStream();

        var thrown = await Assert.ThrowsAsync<StillDecodeException>(() => renderer.RenderAsync(
            LargeImageFixture.Path,
            new StillVariant(2160, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None));

        Assert.IsType<DecodeBudgetExceededException>(thrown.InnerException);
        Assert.Equal(0, renderer.Budget.LiveBytes);
    }

    /// <summary>
    /// Every allowed width, each against the ceiling it actually needs.
    /// <para/>
    /// The numbers are not uniform, and the reason is worth writing down: the JPEG decoder scales
    /// during the IDCT in powers of two, so the decode buffer is the source at 1/8, 1/4 or 1/2
    /// depending on how far the target is below it. For this 8000 × 6000 source, widths up to 1080
    /// land on the 1/4 step (2000 × 1500 × 4 = 12 MB) while 2160 lands on the 1/2 step
    /// (4000 × 3000 × 4 = 48 MB). So <c>w=2160</c> genuinely costs about four times
    /// <c>w=1080</c> — still a quarter of a full decode, and the reason the server's own default
    /// ceiling is 128 MB rather than the 32 MB the headline test uses.
    /// </summary>
    [Theory]
    [InlineData(360, BudgetMegabytes)]
    [InlineData(540, BudgetMegabytes)]
    [InlineData(720, BudgetMegabytes)]
    [InlineData(1080, BudgetMegabytes)]
    [InlineData(1440, 96)]
    [InlineData(2160, 96)]
    public async Task EveryAllowedWidth_RendersInsideItsBudget(int width, int budgetMegabytes)
    {
        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = budgetMegabytes,
        });

        renderer.Budget.ResetPeak();

        using var rendered = new MemoryStream();
        await renderer.RenderAsync(
            LargeImageFixture.Path,
            new StillVariant(width, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
            rendered,
            CancellationToken.None);

        output.WriteLine($"w={width,-5} peak {renderer.Budget.PeakBytes / 1024.0 / 1024.0,6:F1} MB");

        using var check = SKBitmap.Decode(rendered.ToArray());
        Assert.Equal(width, check.Width);
        Assert.Equal(0, renderer.Budget.LiveBytes);
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
        });

        renderer.Budget.ResetPeak();

        using var rendered = new MemoryStream();
        await renderer.RenderAsync(
            LargeImageFixture.Path,
            new StillVariant(320, StillFormat.Jpeg, IsThumb: true, FormatSource.Default),
            rendered,
            CancellationToken.None);

        output.WriteLine($"thumb peak {renderer.Budget.PeakBytes / 1024.0 / 1024.0:F1} MB");

        using var check = SKBitmap.Decode(rendered.ToArray());
        Assert.Equal(320, check.Width);
        Assert.Equal(240, check.Height);
    }

    /// <summary>
    /// Concurrent renders share one ceiling, which is the point: two 48 MP decodes at once is the
    /// spike the budget exists to bound, and the layer would rather queue than swell.
    /// </summary>
    [Fact]
    public async Task ConcurrentRenders_ShareTheOneCeiling()
    {
        using var renderer = new StillRenderer(new MediaOptions
        {
            DecodeMemoryLimitMegabytes = BudgetMegabytes,
            MaxConcurrentDecodes = 2,
        });

        renderer.Budget.ResetPeak();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            using var rendered = new MemoryStream();
            await renderer.RenderAsync(
                LargeImageFixture.Path,
                new StillVariant(i % 2 == 0 ? 720 : 1080, StillFormat.Jpeg, IsThumb: false, FormatSource.Default),
                rendered,
                CancellationToken.None);
            Assert.True(rendered.Length > 0);
        }));

        output.WriteLine($"six concurrent renders peaked at {renderer.Budget.PeakBytes / 1024.0 / 1024.0:F1} MB");

        Assert.Equal(0, renderer.Budget.LiveBytes);
        Assert.True(renderer.Budget.PeakBytes <= BudgetMegabytes * 1024L * 1024L);
    }

    private static long SettledManagedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
