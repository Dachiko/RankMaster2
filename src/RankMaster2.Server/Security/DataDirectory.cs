namespace RankMaster2.Server.Security;

/// <summary>
/// The data directory holds the TLS private key and the device token hashes. Both are
/// credential-grade, so the directory is owner-only and every file written into it is owner-only.
/// On Unix that is enforced; on Windows the per-user LocalApplicationData root already is.
/// </summary>
internal static class DataDirectory
{
    public static void Ensure(string path)
    {
        Directory.CreateDirectory(path);
        RestrictDirectoryToOwner(path);
    }

    public static void RestrictToOwner(string file)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(file)) return;
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // A filesystem that will not carry a mode (a mounted share, FAT) is not a reason to
            // refuse to start; it is a reason not to pretend the mode was applied.
        }
    }

    private static void RestrictDirectoryToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> so a reader never sees a
    /// half-written file, and so the file is never briefly world-readable.
    /// </summary>
    public static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            RestrictToOwner(temporary);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        RestrictToOwner(temporary);
        File.Move(temporary, path, overwrite: true);
        RestrictToOwner(path);
    }
}
