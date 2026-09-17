namespace RankMaster2.Pc.App;

/// <summary>
/// A → D seam granted in PC_CLIENT_PARTS.md's "Rulings" (A-startup-and-shell.md § 7.2): where the
/// pruned libvlc set lives, resolved identically for the index builder (this part) and the engine
/// (part D's <c>LibVlcBackend</c>) so the two never disagree about the directory to the byte.
/// The resolution rule is <c>src/RankMaster2.App/Playback/VlcRuntime.cs</c>'s, unchanged: the
/// shipped layout first, the old build-layout fallback second.
/// </summary>
public static class LibVlcLayout
{
    /// <summary><c>&lt;app dir&gt;\libvlc\win-x64</c>, or the <c>Environment.ProcessPath</c> fallback
    /// if <c>libvlc.dll</c> is not beside <see cref="AppContext.BaseDirectory"/> (VlcRuntime.cs's
    /// rule, verbatim). Null on a non-Windows box where there is no such directory to find — the
    /// caller (D's <c>LibVlcBackend</c>) treats null as "use the system libvlc" (D § 6.2).</summary>
    public static string? NativeDir
    {
        get
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (File.Exists(Path.Combine(candidate, "libvlc.dll")))
                return candidate;

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir is not null)
            {
                var fallback = Path.Combine(exeDir, "libvlc", "win-x64");
                if (File.Exists(Path.Combine(fallback, "libvlc.dll")))
                    return fallback;
            }

            // Not found under either candidate. On win-x64 this is a broken publish (§ 5.4's
            // shape check should have caught it first); on Linux there simply is no such
            // directory. Callers on Windows still get the first candidate so error messages name
            // the expected path rather than a null.
            return OperatingSystem.IsWindows() ? candidate : null;
        }
    }

    public static string? PluginsDir => NativeDir is { } dir ? Path.Combine(dir, "plugins") : null;

    public static string? IndexPath => PluginsDir is { } dir ? Path.Combine(dir, "plugins.dat") : null;

    public static string? StampPath => PluginsDir is { } dir ? Path.Combine(dir, "plugins.stamp") : null;
}
