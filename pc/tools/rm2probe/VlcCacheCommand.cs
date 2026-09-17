namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Diagnostics;
using LibVLCSharp.Shared;

/// <summary>
/// A-startup-and-shell.md § 4.3, § 6.4: the same mechanism as
/// <c>RankMaster2.exe --build-vlc-cache</c> (App/LibVlcIndex.Build) — <c>libvlc_new</c> with
/// <c>--reset-plugins-cache</c> — callable against any directory, and against a *copy* with
/// <c>--copy-to</c> so the kit can build a cache next to the owner's shipped libvlc\ without ever
/// writing into his real install.
/// </summary>
internal static class VlcCacheCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("vlc-cache: needs <libvlcDir>");
            return 1;
        }

        var dir = args[0];
        var copyTo = Program.OptionValue(args, "--copy-to");
        if (copyTo is not null)
        {
            CopyDirectory(dir, copyTo);
            dir = copyTo;
        }

        // Core.Initialize(string) — the directory-taking overload — is Windows/macOS-only;
        // LibVLCSharp throws "not supported on the Linux platform" if it is called with an argument
        // there. On Linux the system's own libvlc is found via the default search path (ldconfig /
        // LD_LIBRARY_PATH), exactly as D-video.md § 6.2's LibVlcBackend does for its Linux branch.
        var pluginsDir = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? Path.Combine(dir, "plugins")
            : "/usr/lib/x86_64-linux-gnu/vlc/plugins";
        var sw = Stopwatch.StartNew();
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                Core.Initialize(dir);
            else
                Core.Initialize();
            using var lib = new LibVLC("--quiet", "--reset-plugins-cache");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vlc-cache: {ex.Message}");
            return 1;
        }
        sw.Stop();

        var indexPath = Path.Combine(pluginsDir, "plugins.dat");
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine("vlc-cache: plugins.dat was not written");
            return 1;
        }

        var bytes = new FileInfo(indexPath).Length;
        Console.WriteLine($"plugins.dat {bytes} bytes in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var subDir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, subDir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
