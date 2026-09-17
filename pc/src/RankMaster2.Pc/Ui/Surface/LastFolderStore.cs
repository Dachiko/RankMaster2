namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// <c>%LOCALAPPDATA%\RankMaster2\last-folder.txt</c> — the old desktop app's file, kept so Resume
/// carries over (plan § 1.1's decisions table). Not the credential directory
/// (<c>RankMaster2.Pc.Link.Paths.DefaultCredentialDirectory</c>, which is <c>...\RankMaster2\pc</c>):
/// the old app wrote directly under <c>RankMaster2\</c> and this is the one file kept at that spelling
/// on purpose, so a machine upgraded from the old client resumes into the same folder.
/// </summary>
public sealed class LastFolderStore
{
    private readonly string _path;

    public LastFolderStore(string? filePath = null) => _path = filePath ?? DefaultPath();

    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "RankMaster2", "last-folder.txt");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(home, "rankmaster2", "last-folder.txt");
    }

    /// <summary>The stored path, or null if nothing is stored, the file cannot be read, or the path
    /// it names is no longer a directory (plan § 6.1: "a stored path to a missing directory → no
    /// Resume"). Never throws.</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var text = File.ReadAllText(_path).Trim();
            return text.Length > 0 && Directory.Exists(text) ? text : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Called only after a successful open (plan § 4.1: "A failed open saves nothing").
    /// Never throws; a failure to remember the folder is not the owner's problem to see.</summary>
    public void Save(string folder)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
