using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>
/// A TLS listener with its own self-signed certificate, unrelated to the real server's — for C6:
/// "something on the LAN answered with a certificate this PC has not paired with." Records whether
/// any bytes arrived after the handshake, so a test can assert the pin mismatch aborted before a
/// single HTTP byte — and therefore the bearer token — ever left the process.
/// </summary>
public sealed class StubTlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private volatile bool _sawApplicationBytes;

    public int Port { get; }
    public bool SawApplicationBytes => _sawApplicationBytes;

    private StubTlsServer(TcpListener listener, X509Certificate2 certificate)
    {
        _listener = listener;
        _certificate = certificate;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    public static StubTlsServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=stub.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Re-import with the private key attached in a form SslStream can use cross-platform.
        var withKey = new X509Certificate2(certificate.Export(X509ContentType.Pfx));

        return new StubTlsServer(listener, withKey);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false,
                SslProtocols.None, checkCertificateRevocation: false).ConfigureAwait(false);

            var buffer = new byte[256];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            readCts.CancelAfter(TimeSpan.FromSeconds(2));
            var read = await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
            if (read > 0) _sawApplicationBytes = true;
        }
        catch
        {
            // The client is expected to abort the handshake or disconnect without sending
            // application data (that is the whole point of this stub); any exception here just
            // means it did, which is success, not failure.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _certificate.Dispose();
        _cts.Dispose();
    }
}
