using RankMaster2.Pc.Link;

namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>Builds a <see cref="SessionLink"/> through its internal constructor (§ 4.3), pointed at
/// <see cref="RealServer"/> and wrapped through a fresh <see cref="TapHandler"/>, with timeouts short
/// enough for a test run but long enough for a real (if loopback) TLS handshake and JSON round trip.</summary>
internal static class LinkFactory
{
    public static LinkOptions DefaultOptions(RealServer server, string? credentialDirectory = null) => new()
    {
        CredentialDirectory = credentialDirectory
            ?? Path.Combine(Path.GetTempPath(), "rm2-link-cred", Guid.NewGuid().ToString("N")),
        ServerDataDirectory = server.DataDirectory,
        StartServerIfNotRunning = false,
        ServerExecutable = null,
        DeviceName = "pc-link-test-" + Guid.NewGuid().ToString("N")[..6],
        ConnectTimeout = TimeSpan.FromSeconds(3),
        ReadTimeout = TimeSpan.FromSeconds(5),
        ActionTimeout = TimeSpan.FromSeconds(6),
        OpenTimeout = TimeSpan.FromSeconds(20),
        OfferTimeout = TimeSpan.FromSeconds(8),
        ServerStartTimeout = TimeSpan.FromSeconds(20),
    };

    public static (SessionLink Link, TapHandler Tap) Build(RealServer server, LinkOptions options)
    {
        var tap = new TapHandler(server);
        var link = new SessionLink(options, wrap: tap.Wrap);
        return (link, tap);
    }

    /// <summary>Convenience for the common case: default options, a fresh tap, nothing else special.</summary>
    public static (SessionLink Link, TapHandler Tap) Build(RealServer server) => Build(server, DefaultOptions(server));
}
