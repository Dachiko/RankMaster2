using RankMaster2.Pc.Link.Enrolment;

namespace RankMaster2.Pc.Link;

/// <summary>Everything the link is told by its host.</summary>
public sealed record LinkOptions
{
    /// <summary>Where link.json lives. Default: %LOCALAPPDATA%\RankMaster2\pc (Linux: $XDG_DATA_HOME/rankmaster2/pc).</summary>
    public string CredentialDirectory { get; init; } = Paths.DefaultCredentialDirectory();

    /// <summary>The running server's data directory (pairing.json). Default: the server's own resolution (§ 2).</summary>
    public string ServerDataDirectory { get; init; } = ServerPaths.DataDirectory();   // class and property differ on purpose: a same-named string property would shadow the type

    /// <summary>Start the server when it is not running. PC_CLIENT_PLAN.md § 13 q.6 default.</summary>
    public bool StartServerIfNotRunning { get; init; } = true;

    /// <summary>The command that starts it. Default on Windows: {app dir}\..\tray\RankMaster2.Tray.exe, no arguments.</summary>
    public string? ServerExecutable { get; init; } = Paths.DefaultTrayExecutable();

    public IReadOnlyList<string> ServerArguments { get; init; } = [];

    /// <summary>Recorded against the token by the server; shown in its device list.</summary>
    public string DeviceName { get; init; } = "PC (" + Environment.MachineName + ")";

    /// <summary>One line per request/response for the host's log file. Never carries a token or a code.</summary>
    public Action<string>? Log { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ServerStartTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OfferTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

public static class SessionLinkFactory
{
    public static ISessionLink Create(LinkOptions options) => new SessionLink(options, wrap: null);
}
