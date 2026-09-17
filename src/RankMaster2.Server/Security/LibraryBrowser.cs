using RankMaster2.Catalog;

namespace RankMaster2.Server.Security;

public sealed record RootEntry(
    string Path,
    string? Label,
    string Kind,
    bool Available,
    long? TotalBytes,
    long? FreeBytes);

public sealed record RootsResponse(IReadOnlyList<RootEntry> Roots);

public sealed record BrowseEntry(
    string Name,
    string Path,
    int? StillCount,
    int? VideoCount,
    bool? Rankable,
    bool HasDatabase,
    bool Accessible);

/// <summary>
/// SERVER_SPEC.md § 10.15, A5: with <c>counts=false</c> the count keys are absent from the wire
/// altogether, not present as <c>null</c> — a root with thousands of children is bytes cheaper that
/// way. A distinct shape rather than an attribute on <see cref="BrowseEntry"/> because the same
/// entry type is also used, with the count fields genuinely <c>null</c>, when a child cannot be
/// enumerated under <c>counts=true</c> — that <c>null</c> must stay on the wire.
/// </summary>
public sealed record BrowseEntryNoCounts(string Name, string Path, bool HasDatabase, bool Accessible);

public sealed record BrowseResponse(string Path, string? Parent, IReadOnlyList<BrowseEntry> Entries);

public sealed record BrowseResponseNoCounts(string Path, string? Parent, IReadOnlyList<BrowseEntryNoCounts> Entries);

public enum BrowseFailure
{
    None,
    NotFound,
    NotADirectory,
    AccessDenied,
}

/// <summary>
/// <see cref="Response"/> is a <see cref="BrowseResponse"/> or a <see cref="BrowseResponseNoCounts"/>
/// depending on the request's <c>counts</c> flag; <c>object</c> rather than a shared base type
/// because ASP.NET Core's JSON writer serialises a value declared as <c>object</c> by its run-time
/// type, so the endpoint needs no branch of its own to pick a shape.
/// </summary>
public sealed record BrowseResult(object? Response, BrowseFailure Failure);

/// <summary>
/// <c>GET /libraries/roots</c> and <c>GET /libraries/browse</c> (SERVER_SPEC.md § 10.14, § 10.15).
///
/// <para>
/// Neither endpoint returns file bytes and neither lists files — only folders, one level, never
/// recursive. That is the boundary that makes "browsing is unrestricted" a defensible product
/// decision: an authenticated caller learns the shape of the filesystem, and reads nothing out of
/// it. Byte-serving lives on <c>/media/*</c> and is confined to the open session folder.
/// </para>
/// </summary>
public sealed class LibraryBrowser
{
    /// <summary>
    /// Files examined in one child before the count is abandoned as unknown. One child of a browsed
    /// folder can be a symlink to something enormous; a bounded enumeration keeps a listing request
    /// from being a denial-of-service primitive against the server's own disk.
    /// </summary>
    private const int MaxFilesCountedPerChild = 200_000;

    private static readonly HashSet<string> PseudoFilesystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "securityfs", "cgroup", "cgroup2", "pstore",
        "bpf", "tracefs", "debugfs", "hugetlbfs", "mqueue", "fusectl", "configfs", "binfmt_misc",
        "autofs", "ramfs", "efivarfs", "nsfs", "selinuxfs", "fuse.gvfsd-fuse", "fuse.portal",
        "squashfs", "overlay", "rpc_pipefs",
    };

    public RootsResponse Roots()
    {
        var entries = new List<RootEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            drives = [];
        }

        foreach (var drive in drives)
        {
            string root;
            try
            {
                root = drive.RootDirectory.FullName;
            }
            catch (Exception)
            {
                continue;
            }

            if (!seen.Add(root)) continue;
            if (IsNoise(drive, root)) continue;

            entries.Add(Describe(drive, root));
        }

        if (!OperatingSystem.IsWindows() && seen.Add(System.IO.Path.DirectorySeparatorChar.ToString()))
            entries.Insert(0, new RootEntry("/", "/", "unknown", Directory.Exists("/"), null, null));

        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new RootsResponse(entries);
    }

    private static RootEntry Describe(DriveInfo drive, string root)
    {
        var available = false;
        try
        {
            available = drive.IsReady;
        }
        catch (Exception)
        {
        }

        string? label = null;
        long? total = null;
        long? free = null;

        if (available)
        {
            // Every one of these throws on a share that dropped between IsReady and the read.
            try { label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel; }
            catch (Exception) { }
            try { total = drive.TotalSize; } catch (Exception) { }
            try { free = drive.AvailableFreeSpace; } catch (Exception) { }
        }

        label ??= root;

        var kind = "unknown";
        try
        {
            kind = drive.DriveType switch
            {
                DriveType.Fixed => "fixed",
                DriveType.Removable or DriveType.CDRom => "removable",
                DriveType.Network => "network",
                DriveType.Ram => "ram",
                _ => "unknown",
            };
        }
        catch (Exception)
        {
        }

        return new RootEntry(root, label, kind, available, total, free);
    }

    private static bool IsNoise(DriveInfo drive, string root)
    {
        if (OperatingSystem.IsWindows()) return false;
        if (root == "/") return false;

        // Linux reports every mount, most of which are kernel bookkeeping rather than a place a
        // photo library could live.
        if (root.StartsWith("/proc", StringComparison.Ordinal) ||
            root.StartsWith("/sys", StringComparison.Ordinal) ||
            root.StartsWith("/dev", StringComparison.Ordinal) ||
            root.StartsWith("/run", StringComparison.Ordinal) ||
            root.StartsWith("/snap", StringComparison.Ordinal) ||
            root.StartsWith("/var/lib/docker", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return PseudoFilesystems.Contains(drive.DriveFormat);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public BrowseResult Browse(string canonicalPath, bool counts)
    {
        if (!Directory.Exists(canonicalPath))
        {
            return File.Exists(canonicalPath)
                ? new BrowseResult(null, BrowseFailure.NotADirectory)
                : new BrowseResult(null, BrowseFailure.NotFound);
        }

        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(canonicalPath).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new BrowseResult(null, BrowseFailure.AccessDenied);
        }
        catch (DirectoryNotFoundException)
        {
            return new BrowseResult(null, BrowseFailure.NotFound);
        }
        catch (IOException)
        {
            return new BrowseResult(null, BrowseFailure.AccessDenied);
        }

        children.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

        // A5: counts=false gets a slimmer entry shape with the count keys absent, not a BrowseEntry
        // full of nulls — the wire-size point only holds if the keys are actually gone.
        object response = counts
            ? new BrowseResponse(canonicalPath, PathGuard.ParentOf(canonicalPath),
                children.Select(DescribeChildWithCounts).ToList())
            : new BrowseResponseNoCounts(canonicalPath, PathGuard.ParentOf(canonicalPath),
                children.Select(DescribeChildNoCounts).ToList());

        return new BrowseResult(response, BrowseFailure.None);
    }

    private static BrowseEntryNoCounts DescribeChildNoCounts(string child)
    {
        var name = NameOf(child);
        var hasDatabase = SafeFileExists(System.IO.Path.Combine(child, JsonCatalog.FileName));

        // counts=false still has to answer `accessible` honestly, so probe with a single directory
        // read rather than a full enumeration.
        return new BrowseEntryNoCounts(name, child, hasDatabase, CanRead(child));
    }

    private static BrowseEntry DescribeChildWithCounts(string child)
    {
        var name = NameOf(child);
        var hasDatabase = SafeFileExists(System.IO.Path.Combine(child, JsonCatalog.FileName));

        var tally = Count(child);
        if (tally is null)
            return new BrowseEntry(name, child, null, null, null, hasDatabase, Accessible: false);

        var (stills, videos, unknownCounts) = tally.Value;
        if (unknownCounts)
            return new BrowseEntry(name, child, null, null, null, hasDatabase, Accessible: true);

        // SPEC.md § Media policy: mixed folder ranks stills only; a videos-only folder ranks videos.
        var eligible = stills > 0 ? stills : videos;
        return new BrowseEntry(name, child, stills, videos, eligible >= 2, hasDatabase, Accessible: true);
    }

    private static string NameOf(string child)
    {
        var name = System.IO.Path.GetFileName(child);
        return string.IsNullOrEmpty(name) ? child : name;
    }

    private static (int Stills, int Videos, bool Unknown)? Count(string folder)
    {
        var stills = 0;
        var videos = 0;
        var examined = 0;

        try
        {
            var info = new DirectoryInfo(folder);
            // IgnoreInaccessible MUST stay false. With it on, opening a directory the process
            // cannot read yields an empty sequence instead of throwing, and the child would be
            // reported as `accessible: true` with a count of 0 — "counted and empty" — when the
            // truth is "unknown". SERVER_SPEC.md § 10.15 draws exactly that distinction.
            foreach (var file in info.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
                ReturnSpecialDirectories = false,
            }))
            {
                if (++examined > MaxFilesCountedPerChild)
                    return (0, 0, true);

                var fileName = file.Name;
                if (fileName.Equals(JsonCatalog.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                FileAttributes attributes;
                try
                {
                    attributes = file.Attributes;
                }
                catch (Exception)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Hidden) != 0) continue;
                if ((attributes & FileAttributes.System) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) continue;

                switch (MediaExtensions.KindOf(fileName))
                {
                    case MediaKind.Still: stills++; break;
                    case MediaKind.Video: videos++; break;
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return null;
        }

        return (stills, videos, false);
    }

    private static bool CanRead(string folder)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            enumerator.MoveNext();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
