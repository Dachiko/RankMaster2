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
