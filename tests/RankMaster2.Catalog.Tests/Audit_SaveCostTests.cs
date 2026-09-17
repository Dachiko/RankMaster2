using System.Diagnostics;
using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Catalog.Tests;

/// <summary>AUDIT THROWAWAY. What one vote costs on disk for a large library (SPEC says 20k is fine).</summary>
public class Audit_SaveCostTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2_000)]
    [InlineData(20_000)]
    public void Cost_of_one_save(int n)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2audit-" + Guid.NewGuid().ToString("N"));
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
