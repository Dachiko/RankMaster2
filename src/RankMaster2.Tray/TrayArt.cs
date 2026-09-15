using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RankMaster2.Tray;

/// <summary>
/// The notification-area icon, drawn in code rather than shipped as a <c>.ico</c>.
/// <para/>
/// It is two panes side by side with the left one lit — the app's entire idea in sixteen pixels —
/// and drawing it here means there is no binary asset to lose, and no icon that silently fails to
/// be embedded by a publish script.
/// </summary>
internal static class TrayArt
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon CreateIcon()
    {
        // 32px covers the notification area at every DPI Windows scales it to from here.
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var lit = new SolidBrush(Color.FromArgb(255, 236, 238, 242));
            using var dim = new SolidBrush(Color.FromArgb(255, 96, 104, 120));
            using var ring = new Pen(Color.FromArgb(255, 46, 204, 113), 2.5f);

            graphics.FillRectangle(lit, 3, 6, 11, 20);
            graphics.FillRectangle(dim, 18, 6, 11, 20);
            graphics.DrawRectangle(ring, 3, 6, 11, 20);
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
}
