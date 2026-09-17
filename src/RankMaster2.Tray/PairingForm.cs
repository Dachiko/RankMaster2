using System.Drawing.Drawing2D;
using QRCoder;

namespace RankMaster2.Tray;

/// <summary>
/// The pairing window: a QR code for the phone's camera, the same credential as six digits for when
/// the camera will not cooperate, and the time left before it stops working.
/// <para/>
/// The code is a bearer secret with a five-minute life (SERVER_SPEC.md § 10.11), so this window is
/// built to be opened, scanned and closed. It never sits there quietly expired: when the window runs
/// out it says so and offers a new one.
/// </summary>
internal sealed class PairingForm : Form
{
    private readonly string _dataDirectory;
    private readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
    private readonly Label _code = new();
    private readonly Label _expiry = new();
    private readonly Label _hint = new();
    private readonly Button _again = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private PairingOffer? _offer;

    /// <summary>Set once the offer file this window is showing has gone from disk — stops the
    /// countdown from overwriting the message with a stale time-left. AUDIT2.md § 3.2/§ 3.3: as of
    /// the server-side fix, guessing wrong — from this phone or from a stranger on the LAN — never
    /// removes this file any more (SERVER_SPEC.md § 10.11's per-address budget already kept the
    /// *window* alive; now the file agrees). The file's disappearance while this dialog is open is
    /// therefore, in the ordinary course of things, exactly one event: a device just paired
    /// successfully with this code. A server shutdown mid-dialog can also take it down, but that is
    /// not something this owner-facing message needs to distinguish.</summary>
    private bool _closedByServer;

    public PairingForm(string dataDirectory)
    {
        _dataDirectory = dataDirectory;

        Text = "Pair a phone — Rank Master 3";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 560);
        BackColor = Color.White;
        // The phone is in the owner's other hand and the PC window is behind everything else.
        TopMost = true;

        _code.Font = new Font(FontFamily.GenericSansSerif, 30f, FontStyle.Bold);
        _code.TextAlign = ContentAlignment.MiddleCenter;
        _code.Dock = DockStyle.Fill;

        _expiry.TextAlign = ContentAlignment.MiddleCenter;
        _expiry.Dock = DockStyle.Fill;
        _expiry.ForeColor = Color.FromArgb(90, 90, 90);

        _hint.Text = "Scan with Rank Master on your phone, or type the code.";
        _hint.TextAlign = ContentAlignment.MiddleCenter;
        _hint.Dock = DockStyle.Fill;
        _hint.ForeColor = Color.FromArgb(120, 120, 120);

        _again.Text = "New code";
        _again.Dock = DockStyle.Fill;
        _again.Click += (_, _) => RequestCode();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = Color.White,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(_qr, 0, 0);
        layout.Controls.Add(_code, 0, 1);
        layout.Controls.Add(_expiry, 0, 2);
        layout.Controls.Add(_hint, 0, 3);
        layout.Controls.Add(_again, 0, 4);
        Controls.Add(layout);

        _tick.Tick += (_, _) => Countdown();
        _tick.Start();

        RequestCode();
    }

    /// <summary>
    /// The wait is for the server's one-second poll to notice the sentinel file, so it happens off
    /// the UI thread: a tray icon whose menu stops responding while it opens a pairing window looks
    /// exactly like a server that has hung.
    /// </summary>
    private async void RequestCode()
    {
        UseWaitCursor = true;
        _again.Enabled = false;
        _expiry.Text = "Asking the server for a code…";

        try
        {
            var directory = _dataDirectory;
            _offer = await Task.Run(() => PairingChannel.Request(directory));

            if (IsDisposed)
                return;

            _closedByServer = false;
            _code.Text = _offer.CodeDisplay;
            _qr.Image?.Dispose();
            _qr.Image = Render(_offer.Payload);
            Countdown();
        }
        catch (Exception e)
        {
            if (IsDisposed)
                return;

            _offer = null;
            _code.Text = "—";
            _expiry.Text = "Could not open a pairing window.";
            MessageBox.Show(this, e.Message, "Rank Master 3", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _again.Enabled = true;
            }
        }
    }

    /// <summary>
    /// Ticks the visible countdown and — the same poll — checks whether the offer file this window
    /// is showing is still the one on disk. AUDIT2.md § 3.2/§ 3.3: this file no longer disappears
    /// just because someone guessed wrong (SERVER_SPEC.md § 10.11's per-address budget locks out
    /// only the guesser, and no longer takes the file down with it), so its disappearance while
    /// this dialog is watching now means the code was used — a device just paired. That is success,
    /// not an attack, and the message below says so instead of accusing the owner of having been
    /// attacked at the exact moment his own phone finished pairing.
    /// </summary>
    private void Countdown()
    {
        if (_offer is null || _closedByServer)
            return;

        if (!File.Exists(PairingChannel.OfferPath(_dataDirectory)))
        {
            _closedByServer = true;
            _expiry.ForeColor = Color.FromArgb(60, 140, 60);
            _expiry.Text = "Paired. You can close this window, or open a new code for another device.";
            return;
        }

        var left = _offer.Remaining(DateTimeOffset.UtcNow);
        if (left <= TimeSpan.Zero)
        {
            _expiry.Text = "This code has expired.";
            _expiry.ForeColor = Color.FromArgb(160, 60, 60);
            return;
        }

        _expiry.ForeColor = Color.FromArgb(90, 90, 90);
        _expiry.Text = $"Expires in {left.Minutes}:{left.Seconds:00}";
    }

    /// <summary>
    /// The QR carries the whole credential — address, port, certificate fingerprint and code — so
    /// one scan both finds the server and pins it. <see cref="PngByteQRCode"/> is used rather than
    /// the System.Drawing renderer because it is the same managed encoder <c>rm2ctl</c> uses, so
    /// both surfaces produce the identical payload.
    /// </summary>
    private static Image Render(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);

        using var stream = new MemoryStream(png);
        using var decoded = Image.FromStream(stream);

        // Copied out of the stream: Image.FromStream keeps the stream alive for the image's life,
        // and this one is disposed on the next line.
        var copy = new Bitmap(decoded.Width, decoded.Height);
        using (var graphics = Graphics.FromImage(copy))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
        }

        return copy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Stop();
            _tick.Dispose();
            _qr.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
