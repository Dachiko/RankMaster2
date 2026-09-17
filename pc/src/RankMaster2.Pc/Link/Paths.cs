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

    /// <summary>
    /// The command that starts the server when it is not running — the whole reason the owner never
    /// has to start anything but this app (§ 5.2.4).
    ///
    /// <para>The published layout puts the tray one level up and across, at
    /// <c>{app dir}\..\tray\RankMaster2.Tray.exe</c>, which is what <c>publish-tray.ps1</c> and
    /// <c>install.ps1</c> between them produce. That is still the answer when it is there. But a
    /// single hard-coded relative path is a brittle thing to hang "he never thinks about the server"
    /// on: one differently-laid-out install and he is back to starting two programs by hand, with a
    /// message telling him to do exactly that. So the near neighbours are tried too, and the first
    /// that actually exists wins.</para>
    ///
    /// <para>When none exists the canonical path is returned anyway, never null: a concrete path is
    /// what <see cref="LinkOptions.ServerExecutable"/> needs in order to try, and what the
    /// "could not be started from …" message needs in order to name somewhere the owner can look.</para>
    /// </summary>
    public static string? DefaultTrayExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var candidate in TrayCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable directory is not this method's problem; try the next one.
            }
        }

        return TrayCandidates().FirstOrDefault();
    }

    /// <summary>
    /// Where the tray may sit, best first: the published layout, then beside the client, then one
    /// and two levels up, which cover a client run from its own build output.
    /// </summary>
    private static List<string> TrayCandidates()
    {
        const string exe = "RankMaster2.Tray.exe";
        var candidates = new List<string>();

        try
        {
            var appDir = AppContext.BaseDirectory;
            foreach (var relative in new[]
                     {
                         Path.Combine("..", "tray", exe),
                         exe,
                         Path.Combine("tray", exe),
                         Path.Combine("..", "..", "tray", exe),
                     })
            {
                candidates.Add(Path.GetFullPath(Path.Combine(appDir, relative)));
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return candidates;
    }
}
