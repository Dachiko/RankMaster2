namespace RankMaster2.Pc.Tests.App;

using System.Reflection;
using Xunit;

/// <summary>A-startup-and-shell.md § 8, test 11.</summary>
public sealed class ManifestTests
{
    [Fact]
    public void Manifest_MatchesPackage()
    {
        var asm = typeof(ManifestTests).Assembly;
        var metadata = asm.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var packageDir = metadata.FirstOrDefault(a => a.Key == "LibVlcPackageDir")?.Value;
        var manifestPath = metadata.FirstOrDefault(a => a.Key == "LibVlcManifestPath")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(packageDir));
        Assert.False(string.IsNullOrWhiteSpace(manifestPath));
        Assert.True(File.Exists(manifestPath), $"manifest not found at {manifestPath}");

        var lines = File.ReadAllLines(manifestPath!)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.True(lines.Count > 0, "plugins.keep.txt has no non-comment lines");
        Assert.Equal(lines.Count, lines.Distinct(StringComparer.Ordinal).Count());

        var buildDir = Path.Combine(packageDir!, "build", "x64");
        foreach (var line in lines)
        {
            var full = Path.Combine(buildDir, line.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"plugins.keep.txt names a file not in VideoLAN.LibVLC.Windows: {line}");
        }
    }
}
