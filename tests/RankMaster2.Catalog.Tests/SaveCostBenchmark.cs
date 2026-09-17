using System.Diagnostics;
using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Catalog.Tests;

/// <summary>
/// What one vote costs on disk, measured rather than guessed (A2). <see cref="JsonCatalog.Save"/>
/// re-reads, re-parses, re-lists the folder, re-serialises and fsyncs on every choice, and SPEC.md
/// says a 20 000-file library is fine — it is fine, and it is not free. The number is what the
/// bounded write-behind (SERVER_SPEC.md § 13.1) is judged against.
///
/// <para>This prints; it never asserts a time. A wall-clock threshold on a shared build box fails for
/// reasons that have nothing to do with the product, and a test that fails for the wrong reason is
/// worse than no test. Run it on its own with
/// <c>dotnet test --filter Category=Benchmark -l "console;verbosity=detailed"</c>.</para>
/// </summary>
public class SaveCostBenchmark(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(2_000)]
    [InlineData(20_000)]
    public void Cost_of_one_save(int n)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-save-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var records = new List<MediaRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var name = $"IMG_{i:D6}.jpg";
            File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1 });
            records.Add(new MediaRecord(new MediaId(name), MediaKind.Still, RankingConstants.DefaultRating, 0, 0, 0));
        }

        var catalog = new JsonCatalog();
        catalog.Save(dir, records);   // first write, file now exists
        var sw = Stopwatch.StartNew();
        for (var k = 0; k < 5; k++)
            catalog.Save(dir, records);
        sw.Stop();
        var size = new FileInfo(Path.Combine(dir, JsonCatalog.FileName)).Length;
        output.WriteLine($"n={n}: {sw.ElapsedMilliseconds / 5.0:0} ms per save, json {size / 1024} KiB (tmpfs/local disk, no USB)");
        Directory.Delete(dir, true);
    }
}
