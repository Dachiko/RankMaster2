using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RankMaster2.Server.Security;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// AUDIT2.md § 2.4: "a damaged devices.json is deliberately quarantined so he can see what
/// happened... but a damaged certificate.pfx is overwritten — the old identity is destroyed even if
/// the read failure was transient." <see cref="TokenStore"/> already quarantines; these prove
/// <see cref="CertificateStore"/> now does the same, and that a genuinely fresh install is not
/// mistaken for a problem.
///
/// Standalone — no <see cref="Harness.Rm2Server"/> needed, each test gets its own throwaway data
/// directory, exactly like <c>PairingWindowSecurityTests</c>'s unit-level probes.
/// </summary>
public sealed class CertificateStoreTests
{
    private static readonly IPAddress ListenAddress = IPAddress.Loopback;

    [Fact]
    public void A_fresh_install_mints_a_certificate_without_being_flagged_as_a_problem()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-cert-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var store = CertificateStore.LoadOrCreate(dataDir, ListenAddress);

            Assert.False(store.RegeneratedAfterProblem,
                "a genuinely fresh install has nothing to have gone wrong with — it must not be reported " +
                "the same way a corrupt or expired certificate is");
            Assert.True(File.Exists(Path.Combine(dataDir, "certificate.pfx")));
            Assert.Empty(Directory.GetFiles(dataDir, "certificate.pfx.corrupt-*"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Reloading_a_good_certificate_keeps_the_same_identity_and_is_not_flagged()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-cert-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var first = CertificateStore.LoadOrCreate(dataDir, ListenAddress);
            var second = CertificateStore.LoadOrCreate(dataDir, ListenAddress);

            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.False(second.RegeneratedAfterProblem);
            Assert.Empty(Directory.GetFiles(dataDir, "certificate.pfx.corrupt-*"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_corrupt_certificate_file_is_quarantined_not_silently_overwritten()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-cert-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "certificate.pfx");

        try
        {
            File.WriteAllBytes(path, "this is not a certificate"u8.ToArray());

            var store = CertificateStore.LoadOrCreate(dataDir, ListenAddress);

            Assert.True(store.RegeneratedAfterProblem,
                "AUDIT2.md § 2.4: a certificate.pfx that was present but unusable must be reported as a " +
                "problem, distinctly from a fresh install with nothing to lose");

            // The original bytes are not gone — moved aside, the same as TokenStore already does for
            // devices.json, rather than silently destroyed by an overwrite.
            var quarantined = Directory.GetFiles(dataDir, "certificate.pfx.corrupt-*");
            Assert.Single(quarantined);
            Assert.Equal("this is not a certificate", File.ReadAllText(quarantined[0]));

            // And a fresh, usable certificate is what is actually loaded — the server still starts.
            Assert.True(File.Exists(path));
            Assert.NotEmpty(store.Fingerprint);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void An_expired_certificate_is_quarantined_and_replaced()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "rm2-cert-expired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "certificate.pfx");

        try
        {
            using (var expired = SelfSignedForTest(
                       notBefore: DateTimeOffset.UtcNow.AddDays(-30),
                       notAfter: DateTimeOffset.UtcNow.AddDays(-1)))
            {
                File.WriteAllBytes(path, expired.Export(X509ContentType.Pkcs12));
            }

            var store = CertificateStore.LoadOrCreate(dataDir, ListenAddress);

            Assert.True(store.RegeneratedAfterProblem);
            Assert.Single(Directory.GetFiles(dataDir, "certificate.pfx.corrupt-*"));

            // The freshly minted certificate is not expired.
            Assert.True(store.Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A minimal self-signed cert with an explicit, possibly-expired validity window — only
    /// what <see cref="CertificateStore.LoadOrCreate"/> looks at (NotBefore/NotAfter/HasPrivateKey);
    /// it is never presented over TLS.</summary>
    private static X509Certificate2 SelfSignedForTest(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=rm2 test"), key, HashAlgorithmName.SHA256);
        using var created = request.CreateSelfSigned(notBefore, notAfter);

        // Round-trip through PFX: CreateSelfSigned's certificate does not reliably report
        // HasPrivateKey the same way a certificate loaded back from a PFX does, and CertificateStore
        // always loads from a PFX on disk.
        var bytes = created.Export(X509ContentType.Pkcs12);
        return new X509Certificate2(bytes, (string?)null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }
}
