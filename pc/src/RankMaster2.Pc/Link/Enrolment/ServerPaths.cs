namespace RankMaster2.Pc.Link.Enrolment;

/// <summary>
/// Where the server keeps <c>pairing.json</c> and <c>pair.request</c> — the server's own data
/// directory, never the client's. Lifted from <c>Rm2SecurityOptions.ResolveDataDirectory()</c>
/// (server) and <c>Pairing.DefaultDataDirectory()</c> (rm2ctl), which agree: the PC must look where
/// the server actually writes.
/// </summary>
internal static class ServerPaths
{
    public static string DataDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("RM2_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv.Trim());

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "RankMaster2", "server");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(home, "rankmaster2", "server");
    }
}
