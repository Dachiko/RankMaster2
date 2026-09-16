namespace RankMaster2.Pc.Link;

/// <summary>The two host-platform defaults <see cref="LinkOptions"/> falls back to.</summary>
public static class Paths
{
    /// <summary>Where <c>link.json</c> lives: <c>%LOCALAPPDATA%\RankMaster2\pc</c> on Windows,
    /// <c>$XDG_DATA_HOME/rankmaster2/pc</c> (else <c>~/.local/share/rankmaster2/pc</c>) elsewhere.</summary>
    public static string DefaultCredentialDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "RankMaster2", "pc");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(home, "rankmaster2", "pc");
    }

    /// <summary>The command that starts the server when it is not running. Default on Windows:
    /// <c>{app dir}\..\tray\RankMaster2.Tray.exe</c>, no arguments. Part A's packaging decides
    /// whether this default path is right relative to the published client (§ 5.2.4); the option on
    /// <see cref="LinkOptions"/> exists so A can override it, and nothing here is verifiable on this
    /// Linux box (acceptance gate item 9).</summary>
    public static string? DefaultTrayExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var appDir = AppContext.BaseDirectory;
            return Path.GetFullPath(Path.Combine(appDir, "..", "tray", "RankMaster2.Tray.exe"));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
