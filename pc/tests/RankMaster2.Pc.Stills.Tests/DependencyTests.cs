using RankMaster2.Pc.Stills;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// Acceptance gate point 10 (pc/plans/C-stills.md section 8): "The production assembly's
/// references are RankMaster2.Core, SkiaSharp and the BCL." No Avalonia, no
/// RankMaster2.Catalog, no server project, no RankMaster2.Ranking, no System.Net.Http -- the whole
/// part must build and run on Linux under xunit with no UI, and the fallback to WPF must not touch
/// it (plan section 1, "Dependencies").
/// </summary>
public class DependencyTests(ITestOutputHelper output)
{
    private static readonly string[] Forbidden =
    [
        "Avalonia",
        "RankMaster2.Catalog",
        "RankMaster2.Server",
        "RankMaster2.Ranking",
        "System.Net.Http",
    ];

    [Fact]
    public void The_production_assembly_references_only_Core_SkiaSharp_and_the_BCL()
    {
        var assembly = typeof(StillSource).Assembly;
        var names = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        output.WriteLine($"{assembly.GetName().Name} references: {string.Join(", ", names)}");

        foreach (var name in names)
        {
            foreach (var forbidden in Forbidden)
            {
                Assert.False(
                    name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{assembly.GetName().Name} references {name}, which starts with forbidden prefix {forbidden}");
            }
        }

        Assert.Contains(names, n => n.Equals("RankMaster2.Core", StringComparison.OrdinalIgnoreCase) || n.Equals("RankMaster2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Equals("SkiaSharp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IMediaProbe_and_MediaProbe_live_in_the_same_dependency_free_assembly()
    {
        // The probe (section 2.2) is this part's too, and must be just as clean.
        Assert.Same(typeof(StillSource).Assembly, typeof(MediaProbe).Assembly);
    }
}
