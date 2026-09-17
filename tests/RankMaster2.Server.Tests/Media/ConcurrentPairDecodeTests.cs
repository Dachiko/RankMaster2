using System.Net;
using System.Text.Json;
using RankMaster2.Server.Media;
using RankMaster2.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// AUDIT2.md § 2.1, over a real host: two genuinely large PNGs decoded concurrently — a pair, the
/// product's whole unit of work, since the ranking screen shows two pictures at once — against a
/// <see cref="DecodeBudget"/> too small to hold both decodes' peaks at the same time.
/// <para/>
/// <b>Before the fix</b> this is not a rare race: <see cref="MediaOptions.MaxConcurrentDecodes"/>
/// admits both into <c>StillRenderer.Render</c>, and whichever calls <c>DecodeBudget.Allocate</c>
/// second gets <see cref="DecodeBudgetExceededException"/>, which <c>MediaEndpoints</c> turned into
/// <c>422 media_decode_failed</c> — "could not be decoded" — for a file that was never the problem.
/// The audit measured 12 refusals out of 12 pairs. The ceiling and image sizes below are chosen so
/// one decode's peak fits comfortably alone and two never fit together, which forces the same
/// collision deterministically rather than leaving it to scheduling luck — the same technique the PC
/// client's <c>ConcurrentPairDecodeTests</c> uses for its identical defect.
/// <para/>
/// <b>After the fix</b> both pictures load: the second decode waits (holding nothing) for the first
/// to finish and free its buffers, rather than being refused. <see cref="StillRenderer.Room"/>'s
/// <c>WaitedAdmissions</c> is asserted &gt; 0 so this test cannot pass by accident if the two requests
/// happen not to overlap — a test that cannot fail if the decodes never actually collide would prove
/// nothing about this finding.
/// </summary>
public class ConcurrentPairDecodeTests(ITestOutputHelper output)
{
    // 3000 x 3000 RGB PNG. PNG has no sampled decode (unlike JPEG, which is cheap at any target via
    // the IDCT) — Skia must decode the whole source — so the decode buffer is the full source at
    // RGBA8888: 3000 * 3000 * 4 = 36,000,000 bytes. w=1080 (the smallest allowed width above the
    // fixtures used elsewhere, from StillVariant.Allowed) needs a further 1080 * 1080 * 4 = 4,665,600
    // byte resample buffer alongside it (StillRenderer.Decode holds both at once — see
    // EstimatePeakBytes), so one render's peak is 40,665,600 bytes (~38.8 MiB).
    private const int Source = 3000;
    private const int TargetWidth = 1080;

    // Comfortably over one peak (~38.8 MiB) so a single decode is never refused; comfortably under
    // two (~77.6 MiB) so a pair always collides. This is deliberately a small, artificial ceiling —
    // it is standing in for AUDIT2.md § 2.1's real-world case (the default 128 MB budget against a
    // pair of 6000×6000+ PNGs), not proposing 50 MB as a real setting.
    private const int BudgetMegabytes = 50;

    [Fact]
    public async Task Both_pictures_of_a_pair_load_when_only_one_fits_the_budget_at_a_time()
    {
        var left = PngWriter.Rgb(Source, Source, (x, y) => ((byte)(x % 256), (byte)(y % 256), (byte)80));
        var right = PngWriter.Rgb(Source, Source, (x, y) => ((byte)80, (byte)(x % 256), (byte)(y % 256)));

        using var session = new StubSession();
        session.Add("left.png", left);
        session.Add("right.png", right);

        var options = new MediaOptions
        {
            DecodeMemoryLimitMegabytes = BudgetMegabytes,
            MaxConcurrentDecodes = 2,
            CacheDirectory = Path.Combine(Path.GetTempPath(), "rm2-pair-cache-" + Guid.NewGuid().ToString("N")[..8]),
        };

        // Built and handed to MediaHost rather than left for MapMediaEndpoints to build its own, so
        // this test can read StillRenderer.Budget / .Room after the requests are done — see
        // MediaHost's `renderer` parameter.
        var renderer = new StillRenderer(options);

        await using var host = new MediaHost(session, options, renderer: renderer);

        var leftTask = host.Client.GetAsync($"/api/v1/media/left.png/still?w={TargetWidth}");
        var rightTask = host.Client.GetAsync($"/api/v1/media/right.png/still?w={TargetWidth}");
        await Task.WhenAll(leftTask, rightTask);

        await AssertDecodedAsync(await leftTask, "left.png");
        await AssertDecodedAsync(await rightTask, "right.png");

        var ceilingBytes = BudgetMegabytes * 1024L * 1024L;
        output.WriteLine($"peak bytes        {renderer.Budget.PeakBytes:N0} (ceiling {ceilingBytes:N0})");
        output.WriteLine($"waited admissions {renderer.Room.WaitedAdmissions}");
        output.WriteLine($"last wait         {renderer.Room.LastWait.TotalMilliseconds:F0} ms");

        // Every byte stayed inside the ceiling: the reservation did not merely paper over an
        // overrun once two decodes were let run over budget.
        Assert.True(renderer.Budget.PeakBytes <= ceilingBytes,
            $"peak {renderer.Budget.PeakBytes:N0} exceeded the {ceilingBytes:N0} byte ceiling");

        // If the two decodes never actually overlapped, this test proved nothing about the finding
        // (see class remarks) — the sizes above are chosen so this should never happen, but assert
        // it rather than assume it.
        Assert.True(renderer.Room.WaitedAdmissions > 0,
            "the two decodes never collided, so nothing here exercised DecodeRoom");
    }

    /// <summary>
    /// Before the fix this is where the test fails: one of the two responses comes back
    /// <c>422 media_decode_failed</c>, <c>"The file is present but could not be decoded as an
    /// image."</c>, for a file <see cref="PngWriter"/> wrote correctly. The message is asserted here
    /// too, because a passing status code alone would not catch a regression that only fixed the
    /// admission and left the "damaged" wording on a capacity refusal.
    /// </summary>
    private static async Task AssertDecodedAsync(HttpResponseMessage response, string id)
    {
        var body = await response.Content.ReadAsByteArrayAsync();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.GetProperty("error");
            Assert.Fail(
                $"{id}: {(int)response.StatusCode} {error.GetProperty("code").GetString()} — " +
                $"{error.GetProperty("message").GetString()}");
        }

        Assert.True(body.Length > 0, $"{id}: 200 OK but an empty body");
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }
}
