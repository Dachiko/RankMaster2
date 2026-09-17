using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace RankMaster2.Server.Security;

/// <summary>
/// The server's TLS identity: generated once, persisted in the data directory, and reused for the
/// life of the install.
///
/// <para>
/// The certificate is self-signed and no client will ever chain it to a root. What makes that safe
/// rather than theatre is that the client pins the SHA-256 of the DER encoding, which it learns out
/// of band from the pairing QR payload and can re-check on every connection via
/// <c>GET /ping</c> (SERVER_SPEC.md § 14). That is the whole security argument, and it only holds
/// while the fingerprint is <b>stable</b> — so this class regenerates only when the file is missing,
/// unreadable or expired, never because the listen address or host name changed. A rotated
/// certificate is a re-pin for every paired device.
/// </para>
/// </summary>
public sealed class CertificateStore
{
    private const string FileName = "certificate.pfx";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(3653); // ~10 years

    public CertificateStore(X509Certificate2 certificate, string fingerprint, bool regeneratedAfterProblem = false)
    {
        Certificate = certificate;
        Fingerprint = fingerprint;
        RegeneratedAfterProblem = regeneratedAfterProblem;
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>Lowercase hex SHA-256 of the DER encoding, with no <c>sha256:</c> prefix.</summary>
    public string Fingerprint { get; }

    /// <summary>The form SERVER_SPEC.md § 14 puts on the wire: <c>sha256:</c> + 64 lowercase hex.</summary>
    public string FingerprintHeaderValue => "sha256:" + Fingerprint;

    /// <summary>
    /// True when <c>certificate.pfx</c> was present but had to be replaced (expired, not yet valid,
    /// no private key, or unreadable) — as opposed to a genuinely fresh install with no file at all.
    /// AUDIT2.md § 2.4: this is exactly the case that strands every paired phone with a "certificate
    /// problem" and no explanation, so the caller uses this to say so loudly rather than at the same
    /// level as an ordinary startup line.
    /// </summary>
    public bool RegeneratedAfterProblem { get; }

    public static CertificateStore LoadOrCreate(string dataDirectory, IPAddress listenAddress, ILogger? logger = null)
    {
        DataDirectory.Ensure(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var existed = File.Exists(path);

        if (existed)
        {
            try
            {
                var loaded = Load(path);
                if (loaded.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1) &&
                    loaded.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                    loaded.HasPrivateKey)
                {
                    return new CertificateStore(loaded, FingerprintOf(loaded));
                }

                loaded.Dispose();
                Quarantine(path, "expired, not yet valid, or has no private key", logger);
            }
            catch (CryptographicException e)
            {
                // Corrupt or unreadable: fall through and mint a new one. The old fingerprint is
                // already unusable, but — unlike the identity it once proved — the bytes themselves
                // are not thrown away: moved aside, the same as TokenStore already does for a
                // devices.json that will not parse (AUDIT2.md § 2.4), rather than silently
                // overwritten. A read failure that was only transient (a sync client mid-write, a
                // momentary lock) is then recoverable by hand instead of having destroyed the
                // identity every paired device trusted.
                logger?.LogWarning(e, "{CertificateFile} could not be read.", path);
                Quarantine(path, "could not be read", logger);
            }
        }

        var created = Create(listenAddress);
        Persist(path, created);
        return new CertificateStore(created, FingerprintOf(created), regeneratedAfterProblem: existed);
    }

    /// <summary>Moves an unusable <c>certificate.pfx</c> aside so it is never silently overwritten.</summary>
    private static void Quarantine(string path, string reason, ILogger? logger)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var quarantined = path + ".corrupt-" + stamp;
            if (File.Exists(quarantined)) File.Delete(quarantined);
            File.Move(path, quarantined);
            logger?.LogWarning(
                "{CertificateFile} was {Reason} and has been moved aside to {QuarantinedFile}; a new " +
                "certificate will be minted. Every paired device will need to re-pair.",
                path, reason, quarantined);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Could not even be moved aside (permissions, a lock). Persist() below will overwrite
            // it in place, which is no worse than the previous, unconditional behaviour.
            logger?.LogWarning(e,
                "{CertificateFile} was {Reason} and could not be moved aside either; it will be " +
                "overwritten.", path, reason);
        }
    }

    public static string FingerprintOf(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    private static X509Certificate2 Create(IPAddress listenAddress)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            new X500DistinguishedName("CN=Rank Master 2 server"),
            key,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        // DigitalSignature only. This is an ECDSA P-256 key; KeyEncipherment is an RSA
        // key-transport bit. Windows Schannel (Kestrel on Windows) aborts the handshake
        // with EOF if that bit is set on an ECC cert.
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(listenAddress);
        if (!listenAddress.Equals(IPAddress.Loopback)) san.AddIpAddress(IPAddress.Loopback);
        if (!listenAddress.Equals(IPAddress.IPv6Loopback)) san.AddIpAddress(IPAddress.IPv6Loopback);
        san.AddDnsName("localhost");
        san.AddDnsName("rankmaster.local");
        var hostName = SafeHostName();
        if (hostName is not null &&
            !hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            san.AddDnsName(hostName);
        }
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Lifetime));
    }

    private static string? SafeHostName()
    {
        try
        {
            var name = Dns.GetHostName();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Persist(string path, X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        var temporary = path + ".tmp";

        File.WriteAllBytes(temporary, pfx);
        DataDirectory.RestrictToOwner(temporary);
        File.Move(temporary, path, overwrite: true);
        DataDirectory.RestrictToOwner(path);
        CryptographicOperations.ZeroMemory(pfx);
    }

    private static X509Certificate2 Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            // Kestrel on Windows cannot use an ephemeral key; everywhere else an ephemeral key is
            // preferable because it never touches a key store on disk beyond the PFX itself.
            var flags = OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
                : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

            return new X509Certificate2(bytes, (string?)null, flags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
