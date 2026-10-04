using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RankMaster2.Tray;

/// <summary>
/// The house style's tokens and drawing helpers for the tray (pc/plans/H-house-style.md § 1, § 3.5):
/// the palette, the Inter fonts, and the few shapes the menu and the QR card are made of.
/// <para/>
/// Every colour the tray draws with comes from here; nothing else in this folder names a colour of
/// its own.
/// </summary>
internal static class House
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#FCFCFA");
    public static readonly Color Ink = ColorTranslator.FromHtml("#141414");
    public static readonly Color OnInk = ColorTranslator.FromHtml("#FCFCFA");
    public static readonly Color Line = ColorTranslator.FromHtml("#DEDEDE");
    public static readonly Color Rule = ColorTranslator.FromHtml("#8C8C88");
    public static readonly Color Accent = ColorTranslator.FromHtml("#E31B23");

    /// <summary>Pixels per logical pixel. The tray is system-DPI aware (<c>Program</c>), so one
    /// number read once is right for the life of the process.</summary>
    public static readonly float Scale = ReadScale();

    private static float ReadScale()
    {
        using var graphics = Graphics.FromHwnd(IntPtr.Zero);
        return graphics.DpiX / 96f;
    }

    /// <summary>A logical size in device pixels.</summary>
    public static int Px(float logical) => (int)Math.Round(logical * Scale);

    public enum Weight
    {
        Regular,
        Medium,
        SemiBold,
    }

    private static PrivateFontCollection? _fonts;

    /// <summary>
    /// A font in pixels (CSS-like sizes, scaled to the screen). The Inter files are embedded in the
    /// exe — the tray ships as a single file, where a loose <c>Fonts</c> folder would not be beside
    /// it — and loaded into a private collection, so nothing is installed on the PC.
    /// <para/>
    /// A private font only draws through GDI+ (<see cref="Graphics.DrawString(string, Font, Brush, PointF, StringFormat)"/>),
    /// not through <c>TextRenderer</c>, which is why the menu renderer and the card draw their own
    /// text. If the fonts cannot be loaded the tray falls back to the system sans-serif: a wrong
    /// typeface is better than no menu.
    /// </summary>
    public static Font CreateFont(Weight weight, float logicalPixels)
    {
        var family = FindFamily(weight);
        return new Font(family, logicalPixels * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static FontFamily FindFamily(Weight weight)
    {
        try
        {
            _fonts ??= LoadFonts();
            var suffix = weight switch { Weight.Medium => "Medium", Weight.SemiBold => "SemiBold", _ => "" };
            foreach (var family in _fonts.Families)
            {
                var isMedium = family.Name.EndsWith("Medium", StringComparison.Ordinal);
                var isSemiBold = family.Name.EndsWith("SemiBold", StringComparison.Ordinal);
                if (suffix.Length == 0 ? !isMedium && !isSemiBold : family.Name.EndsWith(suffix, StringComparison.Ordinal))
                    return family;
            }
        }
        catch (Exception)
        {
            // Fall through to the system font.
        }

        return FontFamily.GenericSansSerif;
    }

    private static PrivateFontCollection LoadFonts()
    {
        var collection = new PrivateFontCollection();
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            // AddMemoryFont keeps pointing at this memory for as long as the collection lives, which
            // is the whole process, so it is deliberately never freed.
            var memory = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            collection.AddMemoryFont(memory, bytes.Length);
        }

        return collection;
    }

    /// <summary>A rounded rectangle; the radius is clamped so a small box never inverts.</summary>
    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        if (r <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }

        var d = r * 2f;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Caps text with wide letter-spacing (the house's caption: Inter Medium 10.5, 0.16 em), centred
    /// in <paramref name="box"/>. GDI+ has no tracking, so each letter is placed by hand.
    /// </summary>
    public static void DrawTracked(Graphics graphics, string text, Font font, Color colour, RectangleF box, float em)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        using var brush = new SolidBrush(colour);

        var tracking = font.Size * em;
        var widths = new float[text.Length];
        var total = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            widths[i] = graphics.MeasureString(text[i].ToString(), font, PointF.Empty, format).Width;
            total += widths[i] + (i < text.Length - 1 ? tracking : 0f);
        }

        var x = box.Left + (box.Width - total) / 2f;
        var y = box.Top + (box.Height - font.GetHeight(graphics)) / 2f;
        for (var i = 0; i < text.Length; i++)
        {
            graphics.DrawString(text[i].ToString(), font, brush, x, y, format);
            x += widths[i] + tracking;
        }
    }

    /// <summary>The house's dotted rule (1.5 px dots).</summary>
    public static void DrawDottedRule(Graphics graphics, Color colour, float x1, float x2, float y)
    {
        using var pen = new Pen(colour, 1.5f * Scale) { DashStyle = DashStyle.Dot };
        graphics.DrawLine(pen, x1, y, x2, y);
    }
}
