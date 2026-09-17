namespace RankMaster2.Pc.App;

using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;

/// <summary>
/// A-startup-and-shell.md § 4.3: the index (<c>plugins.dat</c>) is built on the owner's PC, after
/// the files are in place, by this exe running <c>--build-vlc-cache</c> — <c>libvlc_new</c> with
/// <c>--reset-plugins-cache</c>, which is all <c>vlc-cache-gen.exe</c> ever was. The stamp
/// (<see cref="ComputeStamp"/>/<see cref="IsCurrent"/>/<see cref="WriteStamp"/>) is pure file
/// hashing with no native dependency, so it is exercised directly by the § 8 tests without a real
/// libvlc; only <see cref="Build"/> touches LibVLCSharp.
/// </summary>
public static class LibVlcIndex
{
    public const string PluginsDatName = "plugins.dat";
    public const string StampName = "plugins.stamp";

    /// <summary>
    /// SHA-256 over the sorted lines <c>&lt;relative path&gt;|&lt;size&gt;|&lt;mtime UTC ticks&gt;</c>
    /// of every <c>*.dll</c> under <paramref name="pluginsDir"/>, as lowercase hex, plus the libvlc
    /// version string on a second line (§ 4.3). Order-independent and path-relative by
    /// construction (§ 8 test 7).
    /// </summary>
    public static string ComputeStamp(string pluginsDir, string libvlcVersion)
    {
        var entries = Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories)
            .Select(f =>
            {
                var rel = Path.GetRelativePath(pluginsDir, f).Replace(Path.DirectorySeparatorChar, '/');
                var info = new FileInfo(f);
                return $"{rel}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var joined = string.Join("\n", entries);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        return hash + "\n" + libvlcVersion;
    }

    /// <summary>False if <c>plugins.dat</c> or the stamp is missing, unreadable, or does not match
    /// the plugin directory's current contents — the gate's self-heal trigger (§ 3.3).</summary>
    public static bool IsCurrent(string pluginsDir, string indexPath, string stampPath, string libvlcVersion)
    {
        if (!File.Exists(indexPath) || !File.Exists(stampPath))
            return false;

        string existing;
        try { existing = File.ReadAllText(stampPath); }
        catch { return false; }

        string current;
        try { current = ComputeStamp(pluginsDir, libvlcVersion); }
        catch { return false; }

        return string.Equals(existing.Trim(), current.Trim(), StringComparison.Ordinal);
    }

    public static void WriteStamp(string pluginsDir, string stampPath, string libvlcVersion) =>
        File.WriteAllText(stampPath, ComputeStamp(pluginsDir, libvlcVersion));

    /// <summary>
    /// AUDIT2.md § 3.6. A cheap, file-metadata-only stand-in for "which libvlc build is this",
    /// usable <i>before</i> paying for a native load — which the actual runtime version string
    /// (<c>LibVLC.Version</c>) cannot be, since LibVLCSharp only exposes it as an instance property
    /// that requires constructing a <c>LibVLC</c> (the exact native init this stamp exists to
    /// avoid). Confirmed on the shipped <c>videolan.libvlc.windows</c> 3.0.21 binary: it reports
    /// itself at runtime as <c>"3.0.21 Vetinari"</c> (the codename is baked into both
    /// <c>libvlc.dll</c> and <c>libvlccore.dll</c> as a literal string) — not the bare
    /// <c>"3.0.21"</c> a caller would otherwise have to hard-code and hope matches. Fingerprinting
    /// the two engine binaries' size and write time instead needs no such guess, and is exactly the
    /// same signal <see cref="ComputeStamp"/> already trusts for every plugin DLL: if libvlc is
    /// ever upgraded in place, these files change size or write time along with it.
    /// </summary>
    public static string DescribeLibVlcBuild(string nativeDir) =>
        FingerprintFile(Path.Combine(nativeDir, "libvlc.dll")) + "|" +
        FingerprintFile(Path.Combine(nativeDir, "libvlccore.dll"));

    private static string FingerprintFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "absent";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary><c>RankMaster2.exe --build-vlc-cache</c>'s whole job (§ 4.3), returning the process
    /// exit code and the one line printed to stdout. Exit 2: <c>libvlc.dll</c> missing under
    /// <paramref name="nativeDir"/> (checked before any native call — the only branch the § 8 tests
    /// exercise without a real libvlc). Exit 3: the cache did not appear or is not fresh. Exit 1:
    /// any other native failure.</summary>
    public static LibVlcIndexResult Build(string nativeDir)
    {
        var libvlcDll = Path.Combine(nativeDir, "libvlc.dll");
        if (!File.Exists(libvlcDll))
            return new LibVlcIndexResult(2, $"libvlc.dll not found under {nativeDir}");

        var pluginsDir = Path.Combine(nativeDir, "plugins");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Core.Initialize(string) is Windows/macOS-only — throws "not supported on the Linux
            // platform" there. --build-vlc-cache only ever runs on the owner's Windows PC, but this
            // guard keeps the class harness-safe if it is ever exercised on Linux too.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                Core.Initialize(nativeDir);
            else
                Core.Initialize();
            string? version;
            using (var lib = new LibVLC("--quiet", "--reset-plugins-cache"))
                version = lib.Version;
            sw.Stop();

            var indexPath = Path.Combine(pluginsDir, PluginsDatName);
            if (!File.Exists(indexPath) || new FileInfo(indexPath).Length == 0)
                return new LibVlcIndexResult(3, "plugins.dat was not written");
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(indexPath) > TimeSpan.FromMinutes(1))
                return new LibVlcIndexResult(3, "plugins.dat is not fresh");

            foreach (var tmp in Directory.EnumerateFiles(pluginsDir, PluginsDatName + ".*"))
            {
                try { File.Delete(tmp); }
                catch { /* best-effort cleanup of a CacheSave temp file */ }
            }

            // Stamped with the same file-fingerprint DescribeLibVlcBuild computes before any native
            // call, not the runtime string (version, above) — so VideoEngineGate's next, cheap check
            // agrees with what this expensive rebuild just wrote (§ 3.6).
            WriteStamp(pluginsDir, Path.Combine(pluginsDir, StampName), DescribeLibVlcBuild(nativeDir));

            var bytes = new FileInfo(indexPath).Length;
            var plugins = Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories).Count();
            return new LibVlcIndexResult(0, $"plugins.dat {bytes} bytes, {plugins} plugins, {sw.ElapsedMilliseconds} ms, libvlc {version}");
        }
        catch (Exception ex)
        {
            return new LibVlcIndexResult(1, ex.Message);
        }
    }
}

public readonly record struct LibVlcIndexResult(int ExitCode, string Message);
