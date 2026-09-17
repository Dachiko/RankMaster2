using System.Net;

namespace RankMaster2.Server.Security;

/// <summary>
/// Everything the security layer builds once at startup and then shares. Held by the middleware and
/// the endpoint handlers through closures rather than through DI, because the whole layer is wired
/// from a single post-build call and must not require the host to have been told about it in advance.
/// </summary>
public sealed class SecurityState
{
    public SecurityState(
        Rm2SecurityOptions options,
        string dataDirectory,
        IPAddress listenAddress,
        CertificateStore certificates,
        TokenStore tokens,
        PairingService pairing,
        LibraryBrowser browser,
        ISessionStatusProvider sessions,
        string version)
    {
        Options = options;
        DataDirectory = dataDirectory;
        ListenAddress = listenAddress;
        Certificates = certificates;
        Tokens = tokens;
        Pairing = pairing;
        Browser = browser;
        Sessions = sessions;
        Version = version;
    }

    public Rm2SecurityOptions Options { get; }
    public string DataDirectory { get; }
    public IPAddress ListenAddress { get; }
    public CertificateStore Certificates { get; }
    public TokenStore Tokens { get; }
    public PairingService Pairing { get; }
    public LibraryBrowser Browser { get; }
    public ISessionStatusProvider Sessions { get; }
    public string Version { get; }

    /// <summary>
    /// SERVER_SPEC.md § 14: false while starting or shutting down, and in that window every endpoint
    /// except <c>/ping</c> answers <c>503 server_shutting_down</c>.
    /// </summary>
    public volatile bool Ready;
}
