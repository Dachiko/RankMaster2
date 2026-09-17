namespace RankMaster2.Pc.Tests.App;

using RankMaster2.Pc.App;
using RankMaster2.Pc.Tests.App.Fakes;
using Xunit;

/// <summary>A-startup-and-shell.md § 8, tests 6-8.</summary>
public sealed class LibVlcIndexAndGateTests
{
    [Fact]
    public async Task Gate_RebuildsIndexWhenStale()
    {
        var root = Directory.CreateTempSubdirectory("rm2-gate-test");
        try
        {
            var pluginsDir = Path.Combine(root.FullName, "plugins");
            Directory.CreateDirectory(pluginsDir);
            var dll = Path.Combine(pluginsDir, "a.dll");
            File.WriteAllBytes(dll, new byte[] { 1, 2, 3 });

            var indexPath = Path.Combine(pluginsDir, LibVlcIndex.PluginsDatName);
            var stampPath = Path.Combine(pluginsDir, LibVlcIndex.StampName);

            LibVlcIndex.WriteStamp(pluginsDir, stampPath, "3.0.21");
            File.WriteAllBytes(indexPath, new byte[] { 9 });

            Assert.True(LibVlcIndex.IsCurrent(pluginsDir, indexPath, stampPath, "3.0.21"));

            // Changing the file invalidates the stamp.
            File.WriteAllBytes(dll, new byte[] { 1, 2, 3, 4 });
            Assert.False(LibVlcIndex.IsCurrent(pluginsDir, indexPath, stampPath, "3.0.21"));

            var buildCalls = 0;
            var engine = new FakeVideoSurfaceFactory();
            var gate = new VideoEngineGate(engine, root.FullName, "3.0.21", build: _ =>
            {
                buildCalls++;
                LibVlcIndex.WriteStamp(pluginsDir, stampPath, "3.0.21");
                return new LibVlcIndexResult(0, "stub build");
            });

            await gate.WarmUpAsync();

            Assert.Equal(1, buildCalls);
            Assert.True(LibVlcIndex.IsCurrent(pluginsDir, indexPath, stampPath, "3.0.21"));
            Assert.Equal(1, engine.WarmUpCalls); // the gate still warms the engine after the rebuild
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Stamp_IsOrderIndependent_AndPathRelative()
    {
        var a = Directory.CreateTempSubdirectory("rm2-stampA");
        var b = Directory.CreateTempSubdirectory("rm2-stampB");
        try
        {
            Directory.CreateDirectory(Path.Combine(a.FullName, "sub"));
            Directory.CreateDirectory(Path.Combine(b.FullName, "sub"));

            // Written in a different order, under different absolute paths.
            WriteFixed(Path.Combine(a.FullName, "one.dll"), 10, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            WriteFixed(Path.Combine(a.FullName, "sub", "two.dll"), 20, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            WriteFixed(Path.Combine(b.FullName, "sub", "two.dll"), 20, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            WriteFixed(Path.Combine(b.FullName, "one.dll"), 10, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var stampA = LibVlcIndex.ComputeStamp(a.FullName, "3.0.21");
            var stampB = LibVlcIndex.ComputeStamp(b.FullName, "3.0.21");

            Assert.Equal(stampA, stampB);
        }
        finally
        {
            a.Delete(true);
            b.Delete(true);
        }
    }

    /// <summary>
    /// AUDIT2.md § 3.6: <c>VideoEngineGate</c> used to default its version tag to the literal
    /// <c>"3.0.21"</c>, but the real libvlc 3.0.21 binary reports itself at runtime as
    /// <c>"3.0.21 Vetinari"</c> (confirmed by reading the string straight out of the shipped
    /// <c>libvlc.dll</c>) — a mismatch that made every currency check fail and rebuilt the plugin
    /// index before every first video, every launch, defeating the index the installer built. This
    /// proves the fix end to end through the gate's default path (no version override), across two
    /// separate gates standing in for two separate launches: the second must not rebuild when
    /// nothing about the installed libvlc changed.
    /// </summary>
    [Fact]
    public async Task Gate_DoesNotRebuildOnASecondLaunch_WithNoVersionOverride()
    {
        var root = Directory.CreateTempSubdirectory("rm2-gate-relaunch-test");
        try
        {
            var pluginsDir = Path.Combine(root.FullName, "plugins");
            Directory.CreateDirectory(pluginsDir);
            File.WriteAllBytes(Path.Combine(pluginsDir, "a.dll"), new byte[] { 1, 2, 3 });
            // Stand in for the two real engine binaries VideoEngineGate now fingerprints instead of
            // trusting a runtime-reported version string.
            File.WriteAllBytes(Path.Combine(root.FullName, "libvlc.dll"), new byte[] { 9, 9 });
            File.WriteAllBytes(Path.Combine(root.FullName, "libvlccore.dll"), new byte[] { 9, 9 });

            var indexPath = Path.Combine(pluginsDir, LibVlcIndex.PluginsDatName);
            var stampPath = Path.Combine(pluginsDir, LibVlcIndex.StampName);

            var firstLaunchBuilds = 0;
            var firstEngine = new FakeVideoSurfaceFactory();
            // No libvlcVersion argument: exactly how Composition.Build constructs the real gate.
            var firstGate = new VideoEngineGate(firstEngine, root.FullName, build: _ =>
            {
                firstLaunchBuilds++;
                File.WriteAllBytes(indexPath, new byte[] { 9 });
                LibVlcIndex.WriteStamp(pluginsDir, stampPath, LibVlcIndex.DescribeLibVlcBuild(root.FullName));
                return new LibVlcIndexResult(0, "stub build");
            });

            await firstGate.WarmUpAsync();
            Assert.Equal(1, firstLaunchBuilds); // nothing built yet on this machine — one rebuild, as before

            var secondLaunchBuilds = 0;
            var secondEngine = new FakeVideoSurfaceFactory();
            var secondGate = new VideoEngineGate(secondEngine, root.FullName, build: _ =>
            {
                secondLaunchBuilds++;
                return new LibVlcIndexResult(0, "stub build");
            });

            await secondGate.WarmUpAsync();

            Assert.Equal(0, secondLaunchBuilds); // same install, same files: the index from launch 1 is still good
            Assert.Equal(1, secondEngine.WarmUpCalls); // the gate still warms the engine either way
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeLibVlcBuild_ChangesWhenTheEngineBinaryChanges_StableOtherwise()
    {
        var root = Directory.CreateTempSubdirectory("rm2-describe-build-test");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "libvlc.dll"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(root.FullName, "libvlccore.dll"), new byte[] { 4, 5 });

            var first = LibVlcIndex.DescribeLibVlcBuild(root.FullName);
            var again = LibVlcIndex.DescribeLibVlcBuild(root.FullName);
            Assert.Equal(first, again); // same files: stable across repeated calls, i.e. across launches

            File.WriteAllBytes(Path.Combine(root.FullName, "libvlc.dll"), new byte[] { 1, 2, 3, 4 });
            var afterUpgrade = LibVlcIndex.DescribeLibVlcBuild(root.FullName);
            Assert.NotEqual(first, afterUpgrade); // libvlc.dll replaced: must be treated as a new build
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildVlcCache_ExitCodes_MissingLibVlcDll()
    {
        var dir = Directory.CreateTempSubdirectory("rm2-nolibvlc");
        try
        {
            var result = LibVlcIndex.Build(dir.FullName);
            Assert.Equal(2, result.ExitCode);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static void WriteFixed(string path, int size, DateTime mtimeUtc)
    {
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, mtimeUtc);
    }
}
