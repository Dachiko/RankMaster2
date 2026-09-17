using System.Text.Json;

namespace RankMaster2.Pc.Link.Enrolment;

/// <summary>
/// Where the server keeps <c>pairing.json</c> and <c>pair.request</c> — the server's own data
/// directory, never the client's. SERVER_SPEC.md § 2.4 fixes both the resolution order and the
/// spelling; every program that needs to find it — the tray, the server itself, the PC client's
/// enrolment — MUST agree, or it looks in the wrong place on the very machine the server is on
/// (A28).
/// </summary>
internal static class ServerPaths
{
    /// <summary>§ 2.4's order: <c>RankMaster2:DataDirectory</c> in <c>appsettings.json</c> beside
    /// <paramref name="serverExecutable"/> (the server's own binary — the tray hosts it in-process,
    /// so this is <see cref="Paths.DefaultTrayExecutable"/> unless the caller overrode
    /// <c>LinkOptions.ServerExecutable</c>), then <c>RM2_DATA_DIR</c>, then the platform default
    /// with the § 2.4 spelling.</summary>
    public static string DataDirectory(string? serverExecutable = null)
    {
        if (TryReadFromAppSettings(serverExecutable ?? Paths.DefaultTrayExecutable()) is { } fromConfig)
            return fromConfig;

        var fromEnv = Environment.GetEnvironmentVariable("RM2_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv.Trim());

        return PlatformDefault();
    }

    /// <summary>Reads <c>RankMaster2:DataDirectory</c> out of an <c>appsettings.json</c> beside
    /// <paramref name="serverExecutable"/>, the same key and section the server itself binds
    /// (<c>Rm2SecurityOptions</c>). Returns <c>null</c> — never throws — when there is no
    /// executable to look beside, no file there, or the file does not say.</summary>
    private static string? TryReadFromAppSettings(string? serverExecutable)
    {
        if (string.IsNullOrWhiteSpace(serverExecutable)) return null;

        string? directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(serverExecutable)); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(directory)) return null;

        var appSettingsPath = Path.Combine(directory, "appsettings.json");
        if (!File.Exists(appSettingsPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(appSettingsPath));
            if (document.RootElement.TryGetProperty("RankMaster2", out var section) &&
                section.TryGetProperty("DataDirectory", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } configured)
            {
                return Path.GetFullPath(configured.Trim());
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // Malformed or unreadable appsettings.json: fall through to RM2_DATA_DIR / the default,
            // exactly as an absent or empty DataDirectory key does for the server itself.
        }

        return null;
    }

    /// <summary>The § 2.4 platform default, one spelling: capital <c>R</c>, <c>M</c>, <c>S</c>.</summary>
    private static string PlatformDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "RankMaster2", "Server");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(home, "RankMaster2", "Server");
    }
}
