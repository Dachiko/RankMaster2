using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using QRCoder;

namespace RankMaster2.Tray;

/// <summary>
/// The QR card (plan H § 3.5): a borderless ink card that holds the QR code and nothing else. The
/// phone's camera reads it; there is no six-digit code, no countdown, no hint and no button.
/// <para/>
/// <b>It never shows an expired QR.</b> The server's codes live five minutes (SERVER_SPEC.md § 10.11)
/// and that is not changed. Instead the card asks for a fresh code <see cref="RenewLead"/> before the
/// one on screen runs out. The server accepts a request while a window is open and simply replaces
/// that window (<c>PairingService.OpenWindow</c>), so the new QR is valid the moment it appears and the
/// swap is invisible; the old one is still on screen — and still valid — while the request is in flight.
/// <para/>
/// A card closes by itself when the offer file disappears: a successful pairing removes it, and
/// (since AUDIT2.md § 3.2/§ 3.3) nothing else does. Esc closes it too, and so does right-click, Close.
/// <para/>
/// The card is a layered window with per-pixel alpha, so its rounded corners and soft shadow are
/// smooth rather than a clipped region's stair-steps.
/// </summary>
internal sealed class PairingForm : Form
{
    /// <summary>How long before the shown code runs out a fresh one is requested. The request takes
    /// about a second (the server polls once a second), and 8 s at most before it is given up.</summary>
    private static readonly TimeSpan RenewLead = TimeSpan.FromSeconds(10);

    /// <summary>After a failed renewal, how long to wait before asking again while the old code is
    /// still good.</summary>
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    private enum Phase
    {
        /// <summary>No code to show yet: the red busy line runs.</summary>
        Fetching,
        Showing,
        /// <summary>No code can be had: one accent line says so and the card waits for Esc.</summary>
        Failed,
    }

    private readonly string _dataDirectory;
    private readonly LayeredSurface _surface;
    private readonly Bitmap _cardLayer;
    private readonly Font _captionFont = House.CreateFont(House.Weight.Medium, 10.5f);
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ContextMenuStrip _menu;
    private readonly Font _menuFont;

    private readonly int _margin = House.Px(28);
    private readonly int _cardPadding = House.Px(24);
    private readonly int _chipPadding = House.Px(10);
    private readonly int _qrArea = House.Px(300);

    private Phase _phase = Phase.Fetching;
    private PairingOffer? _offer;
    private Bitmap? _qr;
    private bool _fetching;
    private DateTimeOffset _nextTry;
    private int _missingPolls;

    public PairingForm(string dataDirectory)
    {
        _dataDirectory = dataDirectory;

        var card = _qrArea + 2 * _chipPadding + 2 * _cardPadding;
        var size = card + 2 * _margin;

        Text = "Rank Master 3";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(size, size);
        // The phone is in the owner's other hand and the PC window is behind everything else.
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = false;

        _surface = new LayeredSurface(size, size);
        _cardLayer = BuildCardLayer(size, _margin, card);

        _menuFont = House.CreateFont(House.Weight.Regular, 13);
        _menu = HouseMenu.Create(_menuFont, 132, ("Close", (_, _) => Close()));
        ContextMenuStrip = _menu;

        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        _animation.Tick += (_, _) => Present();
        _animation.Start();

        Fetch();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 1 << 19; // WS_EX_LAYERED
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Present();
    }

    // The window is drawn by UpdateLayeredWindow, never by WM_PAINT.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Drag anywhere moves the card: the window has no title bar to grab.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Asks the server for a pairing window, off the UI thread: the wait is for the server's
    /// one-second poll to notice the sentinel file, and a tray icon whose menu stops responding
    /// while it opens a card looks exactly like a server that has hung.
    /// <para/>
    /// The same method serves the first code and every renewal. While a renewal is in flight the
    /// current QR stays on screen (it is still valid); only when there is nothing valid to show does
    /// the busy line appear.
    /// </summary>
    private async void Fetch()
    {
        if (_fetching)
            return;

        _fetching = true;
        try
        {
            var directory = _dataDirectory;
            var offer = await Task.Run(() => PairingChannel.Request(directory));
            if (IsDisposed)
                return;

            var qr = RenderQr(offer.Payload, _qrArea);
            _qr?.Dispose();
            _qr = qr;
            _offer = offer;
            _phase = Phase.Showing;
            _missingPolls = 0;
        }
        catch (Exception)
        {
            // No message box, no balloon (plan H § 2.2.3). A code that is still good stays on
            // screen and the request is tried again; with nothing good left, the card says so.
            if (IsDisposed)
                return;

            _nextTry = DateTimeOffset.UtcNow + RetryEvery;
            if (_offer is null || _offer.Remaining(DateTimeOffset.UtcNow) <= TimeSpan.Zero)
                _phase = Phase.Failed;
        }
        finally
        {
            _fetching = false;
            if (!IsDisposed)
                Present();
        }
    }

    /// <summary>
    /// Four times a second: close when the offer file is gone, and renew before the code runs out. A file
    /// that is missing for a poll or two is ignored, so a replacement caught half-way never closes
    /// the card.
    /// </summary>
    private void Poll()
    {
        // Only once a code has been shown: before that the offer file may well not exist yet.
        if (_offer is not null && _phase != Phase.Failed)
        {
            if (File.Exists(PairingChannel.OfferPath(_dataDirectory)))
                _missingPolls = 0;
            else if (++_missingPolls >= 3)
            {
                // A device paired with this code. That is the whole of what this card was for.
                Close();
                return;
            }
        }

        if (_phase != Phase.Showing || _offer is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var left = _offer.Remaining(now);

        if (left <= TimeSpan.Zero)
        {
            // Never show an expired QR: with a request in flight the busy line takes its place.
            _phase = _fetching ? Phase.Fetching : Phase.Failed;
            Present();
        }
        else if (left <= RenewLead && !_fetching && now >= _nextTry)
        {
            Fetch();
        }
    }

    /// <summary>The card's busy line sweeps for ~0.9 s each way, ease in and out, so it is drawn
    /// continuously while it is visible and not at all otherwise.</summary>
    private void Present()
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        Draw(_surface.Graphics);
        _surface.Present(Handle);
    }

    private void Draw(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(_cardLayer, 0, 0);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

        var card = _qrArea + 2 * _chipPadding + 2 * _cardPadding;
        var centre = new PointF(_margin + card / 2f, _margin + card / 2f);

        switch (_phase)
        {
            case Phase.Showing when _qr is not null:
            {
                var chip = _qrArea + 2 * _chipPadding;
                var chipRect = new RectangleF(_margin + _cardPadding, _margin + _cardPadding, chip, chip);
                using (var path = House.RoundedRect(chipRect, House.Px(8)))
                using (var brush = new SolidBrush(House.Bg))
                    graphics.FillPath(brush, path);

                // Pixel-exact: the image is a whole number of device pixels per module, centred in
                // its area, never resampled.
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(_qr,
                    (int)(centre.X - _qr.Width / 2f), (int)(centre.Y - _qr.Height / 2f),
                    _qr.Width, _qr.Height);
                break;
            }

            case Phase.Failed:
                House.DrawTracked(graphics, "NO CODE · SERVER NOT ANSWERING", _captionFont, House.Accent,
                    new RectangleF(_margin + _cardPadding, centre.Y - House.Px(20), _qrArea + 2 * _chipPadding, House.Px(40)),
                    0.16f);
                break;

            default:
                DrawBusyLine(graphics, centre);
                break;
        }

        // The animation only has to run while the busy line is on screen.
        var wantAnimation = _phase == Phase.Fetching;
        if (_animation.Enabled != wantAnimation)
            _animation.Enabled = wantAnimation;
    }

    /// <summary>A 1 px hairline with a short red dash sweeping back and forth along it, ~140 px wide.</summary>
    private void DrawBusyLine(Graphics graphics, PointF centre)
    {
        var width = House.Px(140);
        var dash = House.Px(44);
        var left = centre.X - width / 2f;
        var hairline = Math.Max(1, House.Px(1));
        var thickness = Math.Max(2, House.Px(2));

        // 0.9 s each way, there and back: a triangle wave, eased.
        var phase = _clock.Elapsed.TotalSeconds % 1.8 / 0.9;
        var t = (float)(phase <= 1 ? phase : 2 - phase);
        var eased = t * t * (3 - 2 * t);

        graphics.SmoothingMode = SmoothingMode.None;
        using (var line = new SolidBrush(House.Line))
            graphics.FillRectangle(line, left, centre.Y - hairline / 2f, width, hairline);
        using (var red = new SolidBrush(House.Accent))
            graphics.FillRectangle(red, left + (width - dash) * eased, centre.Y - thickness / 2f, dash, thickness);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
    }

    /// <summary>The part of the picture that never changes: the soft shadow, the ink card and its
    /// faint dot grid (7 px apart, white at ~7 %).</summary>
    private static Bitmap BuildCardLayer(int size, int margin, int card)
    {
        var layer = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(layer);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var cardRect = new RectangleF(margin, margin, card, card);
        var radius = House.Px(14);

        // Shadow: a stack of faint rounded rectangles, each a pixel wider, dropped a little.
        var steps = House.Px(20);
        for (var i = 1; i <= steps; i++)
        {
            var rect = cardRect;
            rect.Offset(0, House.Px(8));
            rect.Inflate(i, i);
            using var path = House.RoundedRect(rect, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(3, 0, 0, 0));
            graphics.FillPath(brush, path);
        }

        using var cardPath = House.RoundedRect(cardRect, radius);
        using (var ink = new SolidBrush(House.Ink))
            graphics.FillPath(ink, cardPath);

        graphics.SetClip(cardPath);
        var step = House.Px(7);
        var dot = Math.Max(1.5f, 1.6f * House.Scale);
        using var dots = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
        for (var y = cardRect.Top + step / 2f; y < cardRect.Bottom; y += step)
            for (var x = cardRect.Left + step / 2f; x < cardRect.Right; x += step)
                graphics.FillEllipse(dots, x - dot / 2f, y - dot / 2f, dot, dot);

        return layer;
    }

    /// <summary>
    /// The QR carries the whole credential — address, port, certificate fingerprint and code — so
    /// one scan both finds the server and pins it. <see cref="PngByteQRCode"/> is used rather than
    /// the System.Drawing renderer because it is the same managed encoder <c>rm2ctl</c> uses, so
    /// both surfaces produce the identical payload.
    /// <para/>
    /// Ink on paper, with the quiet zone in the QR image itself: the chip around it is paper and the
    /// card around the chip is dark, and a scanner needs light all round the code, not a dark edge.
    /// The size is a whole number of device pixels per module, as close under <paramref name="area"/>
    /// as that allows.
    /// </summary>
    private static Bitmap RenderQr(string payload, int area)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var code = new PngByteQRCode(data);

        byte[] dark = [House.Ink.R, House.Ink.G, House.Ink.B, 255];
        byte[] light = [House.Bg.R, House.Bg.G, House.Bg.B, 255];

        // One pixel per module tells how many modules there are, quiet zone included.
        using (var probe = Decode(code.GetGraphic(1, dark, light, true)))
        {
            var perModule = Math.Max(1, area / probe.Width);
            return Decode(code.GetGraphic(perModule, dark, light, true));
        }
    }

    private static Bitmap Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var decoded = Image.FromStream(stream);

        // Copied out of the stream: Image.FromStream keeps the stream alive for the image's life,
        // and this one is disposed on return.
        var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
        return copy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _animation.Stop();
            _animation.Dispose();
            _qr?.Dispose();
            _cardLayer.Dispose();
            _surface.Dispose();
            _captionFont.Dispose();
            _menu.Dispose();
            _menuFont.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// A top-down 32-bit DIB section with a GDI+ bitmap drawn straight into its memory, so the same
    /// pixels go to <c>UpdateLayeredWindow</c> with no copy. A <see cref="PixelFormat.Format32bppPArgb"/>
    /// bitmap is premultiplied BGRA — exactly what a layered window with <c>AC_SRC_ALPHA</c> expects.
    /// </summary>
    private sealed class LayeredSurface : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly IntPtr _dc;
        private readonly IntPtr _dib;
        private readonly IntPtr _previous;
        private readonly Bitmap _bitmap;

        public Graphics Graphics { get; }

        public LayeredSurface(int width, int height)
        {
            _width = width;
            _height = height;

            var screen = GetDC(IntPtr.Zero);
            try
            {
                _dc = CreateCompatibleDC(screen);
                var info = new BitmapInfo
                {
                    Size = 40,
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                };
                _dib = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero)
                    throw new InvalidOperationException("The pairing card's surface could not be created.");

                _previous = SelectObject(_dc, _dib);
                _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
                Graphics = Graphics.FromImage(_bitmap);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Present(IntPtr window)
        {
            var size = new Size(_width, _height);
            var source = new Point(0, 0);
            var blend = new Blend { Operation = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(window, IntPtr.Zero, IntPtr.Zero, ref size, _dc, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
        }

        public void Dispose()
        {
            Graphics.Dispose();
            _bitmap.Dispose();
            SelectObject(_dc, _previous);
            DeleteObject(_dib);
            DeleteDC(_dc);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ColorsUsed;
            public int ColorsImportant;
            public int Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blend
        {
            public byte Operation;
            public byte Flags;
            public byte Alpha;
            public byte Format;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, IntPtr destinationPoint,
            ref Size size, IntPtr source, ref Point sourcePoint, int colourKey, ref Blend blend, int flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
    }
}
