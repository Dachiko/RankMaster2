using RankMaster2.Pc.Link;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

/// <summary>
/// The owner starts one program. If the client cannot find the tray it cannot start the server, and
/// he is back to launching two things by hand — so where it looks is worth pinning down.
///
/// <para>These run on any platform for the parts that are platform-independent, and state plainly
/// what they cannot check here: <see cref="Paths.DefaultTrayExecutable"/> returns null off Windows
/// by design, because the tray is a WinForms program that exists nowhere else.</para>
/// </summary>
public class TrayDiscoveryTests
{
    [SkippableFact]
    public void Off_windows_there_is_no_tray_to_find()
    {
        Skip.If(OperatingSystem.IsWindows(), "this is the non-Windows case; Windows is covered below");

        Assert.Null(Paths.DefaultTrayExecutable());
    }

    [SkippableFact]
    public void On_windows_a_concrete_path_is_always_offered()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the tray is a WinForms program; there is none to find here");

        // Never null even when nothing is installed: the caller needs a path to try, and the
        // "could not be started from …" message needs somewhere to name.
        var path = Paths.DefaultTrayExecutable();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.EndsWith("RankMaster2.Tray.exe", path, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(path), "a relative path would depend on the working directory");
    }

    [SkippableFact]
    public void The_published_layout_is_preferred_over_the_neighbours()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the tray is a WinForms program; there is none to find here");

        // The layout publish-tray.ps1 and install.ps1 produce between them: the client in `pc\`,
        // the tray in `tray\` beside it. When it is there, it must win.
        var expected = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "tray", "RankMaster2.Tray.exe"));

        Skip.IfNot(File.Exists(expected), $"no tray published at {expected}; nothing to prefer");

        Assert.Equal(expected, Paths.DefaultTrayExecutable());
    }
}
