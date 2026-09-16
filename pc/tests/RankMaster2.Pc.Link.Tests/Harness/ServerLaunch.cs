namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>
/// Builds the (executable, arguments) pair to hand <c>LinkOptions</c> so the link can start the real
/// server itself (§ 5.2.4, tests C4/C5). <c>ServerProcess.Start</c> takes no environment list —
/// § 4.3's <c>LinkOptions</c> has none, and a published Tray executable needs none, since it always
/// binds its own defaults. A test needs the child to land in the fixture's own isolated data
/// directory and port, and <c>RM2_DATA_DIR</c> in particular is read straight from the OS
/// environment (<c>Rm2SecurityOptions.ResolveDataDirectory</c>), not through any command-line
/// provider — so this wraps the launch in a shell that sets it. A thin shell wrapper is exactly what
/// this seam sees from a real packaged executable too: a path and a list of strings.
/// </summary>
internal static class ServerLaunch
{
    /// <summary>
    /// (Executable, Arguments) that starts <paramref name="dllPath"/> under <paramref name="dotnet"/>
    /// bound to <paramref name="dataDirectory"/> and <paramref name="port"/>. Also writes its PID to
    /// <paramref name="pidFile"/> before <c>exec</c>-replacing the shell with dotnet, so a test can
    /// adopt the real process for cleanup (<c>exec</c> keeps the PID, so the file names the right one).
    /// </summary>
    public static (string Executable, IReadOnlyList<string> Arguments) For(
        string dotnet, string dllPath, string dataDirectory, int port, string pidFile)
    {
        var command =
            $"echo $$ > {Quote(pidFile)}; " +
            $"RM2_DATA_DIR={Quote(dataDirectory)} " +
            $"RankMaster2__Port={port} " +
            "RankMaster2__ListenAddress=127.0.0.1 " +
            "Logging__LogLevel__Default=Warning " +
            "DOTNET_CLI_TELEMETRY_OPTOUT=1 " +
            "RankMaster2__PairingAttemptsPerMinute=100000 " +
            "RankMaster2__PairingWindowAttempts=1000 " +
            $"exec {Quote(dotnet)} {Quote(dllPath)}";

        return ("/bin/sh", new[] { "-c", command });
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
