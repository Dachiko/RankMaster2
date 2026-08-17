using System.IO;

namespace RankMaster2;

internal static class LastFolderStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RankMaster2",
            "last-folder.txt");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var path = File.ReadAllText(FilePath).Trim();
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string folder)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, folder);
    }
}
