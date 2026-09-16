using System.Net;
using System.Net.Security;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RankMaster2.Pc.Link.Transport;

/// <summary>
/// The server presented a certificate that does not match the pin. Never retried, never excused:
/// whatever answered is not the server this PC paired with (§ 5.1.3).
/// <para/>
/// Lifted from <c>src/rm2ctl/Rm2Api.cs</c>'s certificate callback, minus the TOFU and
/// <c>--insecure</c> branches — the PC has a trust root on disk (§ 1) and needs neither.
/// </summary>
public sealed class PinMismatchException : Exception
{
    public string Expected { get; }
    public string Actual { get; }

    public PinMismatchException(string expected, string actual)
        : base($"The server's certificate does not match the one this PC paired with. Expected {expected}, got {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }
}

/// <summary>
/// Builds the one <see cref="SocketsHttpHandler"/> shape every link uses: pin the certificate by
/// SHA-256 of its DER encoding, HTTP/1.1 exact, no proxy, no redirects, no automatic decompression.
/// </summary>
internal static class PinnedHandler
{
    public static SocketsHttpHandler Create(string pinnedFingerprint, TimeSpan connectTimeout)
    {
        var expected = Normalise(pinnedFingerprint);

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout,
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                    throw new PinMismatchException(expected, "(no certificate)");

                var actual = "sha256:" + Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

                var expectedBytes = Encoding.ASCII.GetBytes(expected);
                var actualBytes = Encoding.ASCII.GetBytes(actual);

                var equal = expectedBytes.Length == actualBytes.Length &&
                            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);

                if (!equal)
                    throw new PinMismatchException(expected, actual);

                // SslPolicyErrors is ignored entirely: the certificate names an address the server
                // was configured with, and the fingerprint identifies the machine more tightly than
                // any chain or name check could (§ 5.5).
                return true;
            },
        };

        return handler;
    }

    /// <summary>"sha256:" + lowercase hex, whatever form the caller stored it in. Lifted from
    /// <c>Rm2Api.Normalise</c>.</summary>
    public static string Normalise(string fingerprint)
    {
        var value = fingerprint.Trim().ToLowerInvariant().Replace(":", "", StringComparison.Ordinal);
        if (value.StartsWith("sha256", StringComparison.Ordinal)) value = value["sha256".Length..];
        return "sha256:" + value;
    }

    /// <summary>
    /// Walks <see cref="Exception.InnerException"/> looking for the <see cref="PinMismatchException"/>
    /// the callback above throws. .NET wraps it (typically in an <see cref="AuthenticationException"/>
    /// and then an <see cref="HttpRequestException"/>) on its way out of the handshake; this is the
    /// Kotlin client's <c>isPinMismatch</c>, walked the same way.
    /// </summary>
    public static bool IsPinMismatch(Exception? failure)
    {
        var depth = 0;
        var current = failure;
        while (current is not null && depth++ < 16)
        {
            if (current is PinMismatchException) return true;
            current = current.InnerException;
        }
        return false;
    }
}
