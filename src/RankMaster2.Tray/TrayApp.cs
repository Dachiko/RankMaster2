using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RankMaster2.Server.Security;
using RankMaster2.Server.Sessions;

namespace RankMaster2.Tray;

/// <summary>
/// The notification-area icon and its menu. Deliberately small: the phone is the interface, and
/// anything that needs a screen on the PC is a thing the phone should have been able to do.
/// <para/>
/// The menu is what the owner cannot do from the phone — hand the phone a pairing credential, and
/// stop the server.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private readonly string _dataDirectory;
    private readonly ILogger _logger;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _session;
    private readonly ToolStripMenuItem _deviceWarning;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly string _listenAddress;
    private readonly int _port;
    private readonly string _fingerprint;
    private int _refreshInFlight;
    private PairingForm? _pairing;

    public TrayApp(WebApplication app, string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _logger = app.Logger;

        var options = new Rm2SecurityOptions();
        app.Configuration.GetSection(Rm2SecurityOptions.SectionName).Bind(options);
        var listenAddress = options.ResolveListenAddress();
        _listenAddress = listenAddress.ToString();
        _port = options.Port;
        _fingerprint = ReadFingerprint(dataDirectory);

        // A loopback bind is by design (SERVER_RUNNING.md), but the QR it produces carries
        // host=127.0.0.1 and a phone that tries it fails with a Wi-Fi-looking error (A29). Say so
        // here, where the owner is looking when something is wrong.
        var statusText = IPAddress.IsLoopback(listenAddress)
            ? $"Listening on {_listenAddress}:{_port} — this PC only; phones cannot connect (see SERVER_RUNNING.md)"
            : $"Listening on {_listenAddress}:{_port}";

        _status = new ToolStripMenuItem(statusText) { Enabled = false };
        _session = new ToolStripMenuItem("No folder open") { Enabled = false };
        _deviceWarning = new ToolStripMenuItem("Device list was unreadable — pair the phone again")
        {
            Enabled = false,
            Visible = false,
        };

        var menu = new ContextMenuStrip();
        _menu = menu;
        menu.Items.Add(_status);
        menu.Items.Add(_session);
        menu.Items.Add(_deviceWarning);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show pairing QR…", null, (_, _) => ShowPairing());
        menu.Items.Add("Copy certificate fingerprint", null, (_, _) => CopyFingerprint());
        menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _icon = new NotifyIcon
        {
            Icon = TrayArt.CreateIcon(),
            // NotifyIcon truncates past 63 characters, so the address is all that fits. The folder
            // lives in the menu, where there is room for it.
            Text = Shorten($"Rank Master 3 — {_listenAddress}:{_port}"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowPairing();

        // One second is imperceptible for a status line. The read usually costs one lock-free
        // field, but its rare contended path can block, so it runs off this thread (A34).
        _poll = new System.Windows.Forms.Timer { Interval = 1000 };
        _poll.Tick += (_, _) => RefreshSession();
        _poll.Start();
        RefreshSession();
    }

    /// <summary>
    /// Asks the server for a pairing window and shows it. The request goes through the sentinel
    /// file in the data directory — the channel SERVER_SPEC.md § 10.1.1 defines — rather than a
    /// direct call, even though the server is in this very process. That channel is what proves the
    /// asker controls the owner's OS account, it is already covered by the server's tests, and
    /// using it here means the tray keeps working unchanged if the server ever moves to a service.
    /// </summary>
    private void ShowPairing()
    {
        if (_pairing is { IsDisposed: false })
        {
            _pairing.Activate();
            return;
        }

        _pairing = new PairingForm(_dataDirectory);
        _pairing.FormClosed += (_, _) => _pairing = null;
        _pairing.Show();
        _pairing.Activate();
    }

    private void CopyFingerprint()
    {
        if (string.IsNullOrEmpty(_fingerprint))
        {
            Balloon("The certificate fingerprint is not readable yet.");
            return;
        }

        Clipboard.SetText(_fingerprint);
        Balloon("Certificate fingerprint copied.");
    }

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_dataDirectory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Balloon($"The server's data folder could not be opened ({_dataDirectory}). " +
                "Check that the folder exists and is writable.");
        }
    }

    /// <summary>
    /// Stops the message loop; <c>Program</c> then shuts the server down gracefully. No
    /// confirmation prompt and no "unsaved work" warning: there is never any. Every 2xx the server
    /// sent was on disk before it was sent (SERVER_SPEC.md § 13.1), so the worst an exit can cost
    /// is the pair currently on the phone's screen.
    /// </summary>
    private void Exit()
    {
        _icon.Visible = false;
        Application.ExitThread();
    }

    /// <summary>
    /// Reads the shared session and the device store off the UI thread. <see
    /// cref="SessionRegistry.CurrentForMedia"/> is usually a lock-free field read, but its
    /// contended fallback can wait up to five seconds and then throw <see
    /// cref="TimeoutException"/> (A34) — on the timer tick that would freeze the whole menu for
    /// as long as it took. A poll already in flight is skipped rather than piled up.
    /// </summary>
    private void RefreshSession()
    {
        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
            return;

        Task.Run(() =>
        {
            string? sessionText = null;
            try
            {
                var view = SessionRegistry.Shared.CurrentForMedia;
                sessionText = view is null ? "No folder open" : "Ranking: " + FolderLabel(view.Folder);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Reading the ranking session for the tray failed; keeping the previous status.");
            }

            var deviceStoreCorrupt = HasCorruptDeviceStore();

            PostToUi(() =>
            {
                if (sessionText is not null)
                    _session.Text = sessionText;
                _deviceWarning.Visible = deviceStoreCorrupt;
            });

            Volatile.Write(ref _refreshInFlight, 0);
        });
    }

    /// <summary>H12: a device store the last load could not parse is moved aside by the security
    /// layer as <c>devices.json.corrupt-&lt;timestamp&gt;</c>, never silently emptied. The tray
    /// says so rather than leave it to the log.</summary>
    private bool HasCorruptDeviceStore()
    {
        try
        {
            return Directory.Exists(_dataDirectory) &&
                Directory.EnumerateFiles(_dataDirectory, "devices.json.corrupt-*").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void PostToUi(Action action)
    {
        try
        {
            if (_menu.IsHandleCreated)
                _menu.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // The tray is shutting down.
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call.
        }
    }

    private void Balloon(string text)
    {
        _icon.BalloonTipTitle = "Rank Master 3";
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    private static string FolderLabel(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private static string Shorten(string text) => text.Length <= 63 ? text : text[..63];

    /// <summary>
    /// The fingerprint the phone pins. Read from the certificate the server just loaded, using the
    /// server's own helper so the two can never compute it differently — it is the string a
    /// mismatch would strand a phone on.
    /// </summary>
    private static string ReadFingerprint(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "certificate.pfx");
            if (!File.Exists(path))
                return "";

            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(path);
            return "sha256:" + CertificateStore.FingerprintOf(certificate);
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
        _pairing?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
