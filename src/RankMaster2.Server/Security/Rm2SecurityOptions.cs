using System.Net;

namespace RankMaster2.Server.Security;

/// <summary>
/// Everything the security and transport layer reads out of configuration.
/// Bound from the <c>RankMaster2</c> configuration section; every key may also be supplied as an
/// environment variable (<c>RankMaster2__ListenAddress</c>, …) or on the command line.
///
/// SERVER_SPEC.md § 2 fixes two of these as contract, not preference:
/// the bind address is an <b>explicit</b> LAN address and is never <c>0.0.0.0</c>, and the scheme
/// is https with a self-signed certificate pinned by fingerprint.
/// </summary>
public sealed class Rm2SecurityOptions
{
    public const string SectionName = "RankMaster2";

    /// <summary>
    /// The single IP address Kestrel binds. MUST be an explicit address; the wildcards
    /// <c>0.0.0.0</c>, <c>::</c>, <c>*</c> and <c>+</c> are rejected at startup rather than
    /// quietly accepted, because "listen on everything" is exactly the mistake that turns a LAN
    /// tool into an internet-facing filesystem browser (SERVER_SPEC.md § 2).
    /// Defaults to loopback, which is the only address that is safe without the operator saying so.
    /// </summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>TLS port. 18611. Out of the way of the crowded 8xxx range, where it collided with an unrelated service.</summary>
    public int Port { get; set; } = 18611;

    /// <summary>
    /// Where the certificate and the device store live. Empty means the per-user default:
    /// <c>%LOCALAPPDATA%\RankMaster2\Server</c> on Windows, <c>$XDG_DATA_HOME/RankMaster2/Server</c>
    /// (or <c>~/.local/share/RankMaster2/Server</c>) elsewhere. SERVER_SPEC.md § 2.4: one spelling,
    /// capital <c>R</c>, <c>M</c>, <c>S</c> — a lowercase <c>server</c> or <c>rankmaster2</c> here
    /// silently split this server's own state across two directories on Linux (C12).
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>
    /// How long a pairing window stays open. SERVER_SPEC.md § 10.11 and § 15 fix this at 5 minutes.
    /// Configurable only downwards; a larger value is clamped.
    /// </summary>
    public int PairingWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Wrong guesses a single pairing window tolerates before it is destroyed. Reported to the
    /// client as <c>details.attemptsRemaining</c> on <c>invalid_pairing_code</c>.
    /// This is the cap that actually makes a six-digit code safe: the per-address rate limit only
    /// slows an attacker down, this one stops them.
    /// </summary>
    public int PairingWindowAttempts { get; set; } = 5;

    /// <summary>Pairing attempts per minute per source address (SERVER_SPEC.md § 15).</summary>
    public int PairingAttemptsPerMinute { get; set; } = 5;

    /// <summary>
    /// Open a pairing window automatically at startup when no device is enrolled. Without this a
    /// fresh install has no way in at all, since opening a window is deliberately not an HTTP
    /// operation.
    /// </summary>
    public bool AutoOpenPairingWhenUnenrolled { get; set; } = true;

    /// <summary>
    /// Watch <c>&lt;data&gt;/pair.request</c> and open a pairing window when it appears, and
    /// <c>&lt;data&gt;/revoke.request</c> and revoke the device it names (AUDIT2.md § 3.13). Both are
    /// the out-of-band channel SERVER_SPEC.md § 10.11 / § 10.1.1 assumes: reachable only by a
    /// process running as the same OS user, never over the network.
    /// </summary>
    public bool WatchPairRequestFile { get; set; } = true;

    /// <summary>Issued device tokens never expire by default (<c>expiresAt: null</c>).</summary>
    public int? TokenLifetimeDays { get; set; }

    /// <summary>
    /// SERVER_SPEC.md § 2.4, § 13.1: how long a choice may sit in memory before the database is
    /// written. <c>0</c> restores the old behaviour exactly — every choice is saved before its
    /// response leaves. Not on <c>/ping</c>; a client neither negotiates this nor needs to know it.
    /// </summary>
    public int SaveDelaySeconds { get; set; } = 2;

    /// <summary>
    /// SERVER_SPEC.md § 2.4, § 13.1: how many choices may sit unsaved before a write is forced,
    /// whichever of this and <see cref="SaveDelaySeconds"/> is reached first.
    /// </summary>
    public int MaxUnsavedChoices { get; set; } = 5;

    /// <summary>
    /// Resolves <see cref="DataDirectory"/>, honouring the <c>RM2_DATA_DIR</c> environment variable
    /// when configuration says nothing.
    /// </summary>
    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
            return Path.GetFullPath(DataDirectory.Trim());

        var fromEnv = Environment.GetEnvironmentVariable("RM2_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv.Trim());

        // SERVER_SPEC.md § 2.4: one spelling everywhere, "RankMaster2/Server" — capital R, M, S.
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

    /// <summary>
    /// Parses <see cref="ListenAddress"/> and refuses every form of "any address".
    /// </summary>
    public IPAddress ResolveListenAddress()
    {
        var raw = (ListenAddress ?? "").Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException(
                "RankMaster2:ListenAddress is empty. Set an explicit LAN address (SERVER_SPEC.md § 2).");

        if (raw is "*" or "+" || raw.Equals("any", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"RankMaster2:ListenAddress '{raw}' is a wildcard. SERVER_SPEC.md § 2 requires an explicit address.");

        if (!IPAddress.TryParse(raw, out var address))
            throw new InvalidOperationException(
                $"RankMaster2:ListenAddress '{raw}' is not an IP address. A host name is not accepted: the address " +
                "that gets bound must be unambiguous.");

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException(
                $"RankMaster2:ListenAddress '{raw}' binds every interface. SERVER_SPEC.md § 2 forbids it: the bearer " +
                "token is the only barrier between the network and the whole filesystem.");

        return address;
    }

    public TimeSpan PairingWindow =>
        TimeSpan.FromSeconds(Math.Clamp(PairingWindowSeconds, 30, 300));
}
