namespace RankMaster2.Catalog;

public static class FileOps
{
    public const string DiscardedFolder = "discarded";
    public const string SpecialFolder = "special 1";
    public const string BackupPrefix = "rankmaster_backup_";

    public static string UniqueFileName(string directory, string desiredName)
    {
        var dest = Path.Combine(directory, desiredName);
        if (!File.Exists(dest) && !Directory.Exists(dest))
            return desiredName;

        var ext = Path.GetExtension(desiredName);
        var stem = Path.GetFileNameWithoutExtension(desiredName);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!File.Exists(Path.Combine(directory, candidate)))
                return candidate;
        }

        throw new IOException("Could not find a unique filename in " + directory);
    }

    public static string MoveToSubfolder(string folder, string filename, string subfolder, int retries = 20)
    {
        var source = Path.Combine(folder, filename);
        if (!File.Exists(source))
            throw new FileNotFoundException("Media file is gone.", source);

        var destDir = Path.Combine(folder, subfolder);
        Directory.CreateDirectory(destDir);
        var destName = UniqueFileName(destDir, filename);
        MoveWithRetry(source, Path.Combine(destDir, destName), retries);
        return destName;
    }

    public static void MoveWithRetry(string source, string dest, int retries = 20)
    {
        IOException? last = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                File.Move(source, dest);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(50);
            }
        }

        throw last ?? new IOException("Move failed: " + source);
    }

    public static string BackupLibrary(string folder, DateTimeOffset now)
    {
        var name = BackupPrefix + now.ToString("yyyyMMdd_HHmmss");
        var backup = Path.Combine(folder, name);
        Directory.CreateDirectory(backup);

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var file = Path.GetFileName(path);
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(path, Path.Combine(backup, file), overwrite: true);
        }

        return backup;
    }

    public static void RestoreLibrary(string folder, string backup)
    {
        if (!Directory.Exists(backup))
            throw new DirectoryNotFoundException(backup);

        var backupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(backup))
        {
            var name = Path.GetFileName(path);
            backupNames.Add(name);
            File.Copy(path, Path.Combine(folder, name), overwrite: true);
        }

        foreach (var path in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!backupNames.Contains(name))
                File.Delete(path);
        }
    }

    public static void WaitUntilUnlocked(string path, int retries = 40)
    {
        if (!File.Exists(path))
            return;

        IOException? last = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(50);
            }
        }

        throw last ?? new IOException("File still locked: " + path);
    }

    /// <summary>
    /// Two-phase rename to 000001.ext by conservative score. Returns old→new ids.
    /// </summary>
    public static Dictionary<MediaId, MediaId> RenameByConservativeScore(
        string folder,
        IReadOnlyList<MediaRecord> records)
    {
        var ordered = records
            .OrderByDescending(r => r.Rating.ConservativeScore)
            .ThenBy(r => r.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var temps = new List<(MediaRecord Record, string TempName)>();
        foreach (var record in ordered)
        {
            var ext = Path.GetExtension(record.Filename);
            var temp = $"__rm2_{Guid.NewGuid():N}{ext}";
            MoveWithRetry(Path.Combine(folder, record.Filename), Path.Combine(folder, temp));
            temps.Add((record, temp));
        }

        var map = new Dictionary<MediaId, MediaId>();
        var index = 1;
        foreach (var (record, temp) in temps)
        {
            var next = $"{index:D6}{Path.GetExtension(record.Filename)}";
            MoveWithRetry(Path.Combine(folder, temp), Path.Combine(folder, next));
            map[record.Id] = new MediaId(next);
            index++;
        }

        return map;
    }
}

public readonly record struct MovedFile(
    string Folder,
    string Subfolder,
    string OriginalName,
    string DestName,
    MediaRecord Record)
{
    public string SourcePath => Path.Combine(Folder, OriginalName);
    public string DestPath => Path.Combine(Folder, Subfolder, DestName);
}
