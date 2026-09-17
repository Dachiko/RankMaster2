namespace RankMaster2.Pc.Tests.App;

using RankMaster2.Pc.App;
using Xunit;

/// <summary>G-audit-remediation.md § 3.7, A25: a crash file written beside the exe never survives
/// the next <c>install.ps1</c> run (step 5 renames that whole directory to <c>.old</c> and deletes
/// it). <see cref="CrashLog.Write"/> now takes the directory explicitly so this is provable without
/// touching the real <c>%LOCALAPPDATA%</c>.</summary>
public sealed class CrashLogTests
{
    [Fact]
    public void Write_NeverUsesTheInstallDirectory()
    {
        // AppContext.BaseDirectory is exactly the directory install.ps1 destroys and rebuilds.
        var path = CrashLog.Write(new InvalidOperationException("boom"), terminating: false,
            directory: Path.Combine(Path.GetTempPath(), $"rm2-crashlog-{Guid.NewGuid():N}"));
        try
        {
            Assert.False(
                path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                $"crash file must not live under the install directory: {path}");
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultDirectory_IsNotTheInstallDirectory()
    {
        var dir = CrashLog.DefaultDirectory();
        Assert.False(
            dir.StartsWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
            $"default crash directory must not be under the install directory: {dir}");
    }

    [Fact]
    public void Write_CreatesTheDirectoryIfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rm2-crashlog-missing-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(dir));

        var path = CrashLog.Write(new Exception("x"), terminating: true, directory: dir);
        try
        {
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_SeveralCrashesBackToBack_NeverOverwriteEachOther()
    {
        // A25: two crashes in one second used to overwrite each other outright (second-precision
        // names). Milliseconds narrow that a great deal but a tight back-to-back burst (as an
        // AppDomain.UnhandledException immediately followed by an UnobservedTaskException could
        // produce) can still land in the same millisecond — writing several in a row, with no
        // delay between them, is this test's best shot at provoking exactly that collision on a
        // fast box, and UniquePath's numeric suffix must win it every time regardless.
        var dir = Path.Combine(Path.GetTempPath(), $"rm2-crashlog-collide-{Guid.NewGuid():N}");
        try
        {
            var paths = new List<string>();
            for (var i = 0; i < 8; i++)
                paths.Add(CrashLog.Write(new Exception($"crash-{i}"), terminating: i % 2 == 0, directory: dir));

            Assert.Equal(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
            for (var i = 0; i < paths.Count; i++)
            {
                Assert.True(File.Exists(paths[i]));
                Assert.Contains($"crash-{i}", File.ReadAllText(paths[i]));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_NeverThrows_EvenWhenTheDirectoryCannotBeCreated()
    {
        // A file where a directory is expected: Directory.CreateDirectory throws IOException.
        var blocker = Path.Combine(Path.GetTempPath(), $"rm2-crashlog-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var path = CrashLog.Write(new Exception("x"), terminating: false,
                directory: Path.Combine(blocker, "pc"));
            Assert.NotNull(path); // never throws; the file simply may not exist
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
