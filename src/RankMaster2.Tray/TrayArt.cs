using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace RankMaster2.Tray;

/// <summary>
/// The notification-area icon, drawn in code rather than shipped as a <c>.ico</c> (the exe's own
/// icon, <c>App.ico</c>, is a separate file wired in the csproj).
/// <para/>
/// It is the house two-pane mark (plan H § 3.5): two upright panes, the left one filled, the right
/// one an outline. The left pane turns red while a folder is being ranked — the one fact the icon
/// has to give.
/// <para/>
/// 3.4.2: two fixes after Mike saw a near-invisible icon in his tray. (1) The panes follow the
/// taskbar's theme: ink on a light taskbar (his), paper on a dark one — 3.3.0 drew paper only, which
/// is white on white in the light taskbar and its overflow flyout. (2) The mark is set pixel by pixel
/// on a grid made for each size, every pixel fully opaque or fully clear: the smooth, anti-aliased
/// drawing it replaced went soft at 16–20 px, and <c>GetHicon</c> mangles half-transparent pixels.
/// </summary>
internal static class TrayArt
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>True when Windows draws the taskbar light (Settings → Personalisation → Colours,
    /// "Choose your default Windows mode"). Missing key or value = dark, Windows' default.</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <param name="folderOpen">True while a folder is open: the left pane is accent.</param>
    /// <param name="lightTaskbar">True on a light taskbar: the panes are ink, else paper.</param>
    public static Icon CreateIcon(bool folderOpen, bool lightTaskbar)
    {
        // The size the notification area asks for at this DPI (16 at 100 %, 20 at 125 %, ...).
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var g = GridFor(size);

        var outline = lightTaskbar ? House.Ink : House.OnInk;
        var fill = folderOpen ? House.Accent : outline;

        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (var pane = 0; pane < 2; pane++)
        {
            var left = g.X0 + pane * (g.Width + g.Gap);
            for (var y = 0; y < g.Height; y++)
            {
                for (var x = 0; x < g.Width; x++)
                {
                    // The four corner pixels stay clear: the pane reads as rounded, with no
                    // half-transparent pixel anywhere.
                    var cornerX = x == 0 || x == g.Width - 1;
                    var cornerY = y == 0 || y == g.Height - 1;
                    if (cornerX && cornerY) continue;

                    var onBorder = x < g.Stroke || x >= g.Width - g.Stroke || y < g.Stroke || y >= g.Height - g.Stroke;
                    if (pane == 0) bitmap.SetPixel(left + x, g.Y0 + y, fill);
                    else if (onBorder) bitmap.SetPixel(left + x, g.Y0 + y, outline);
                }
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle does not own the handle, so the icon is cloned and the handle is
            // released here. Leaking one HICON per start is the kind of thing that only shows up
            // after the app has been restarted a few hundred times.
            using var unowned = Icon.FromHandle(handle);
            return (Icon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Pane width and height, gap between the panes, outline width, and the top-left corner,
    /// centred in a <paramref name="size"/> square. Hand-set for the sizes Windows uses at 100–200 %;
    /// any other size gets the same proportions, rounded to whole pixels.</summary>
    private static (int Width, int Height, int Gap, int Stroke, int X0, int Y0) GridFor(int size) => size switch
    {
        16 => (6, 12, 2, 1, 1, 2),
        20 => (7, 14, 2, 1, 2, 3),
        24 => (8, 18, 2, 2, 3, 3),
        32 => (11, 22, 4, 2, 3, 5),
        _ => Proportional(size),
    };

    private static (int, int, int, int, int, int) Proportional(int size)
    {
        var width = (int)Math.Round(size * 0.35);
        var gap = Math.Max(2, (int)Math.Round(size * 0.1));
        var height = (int)Math.Round(size * 0.7);
        var stroke = size >= 24 ? Math.Max(2, size / 16) : 1;
        return (width, height, gap, stroke, (size - (2 * width + gap)) / 2, (size - height) / 2);
    }
}
