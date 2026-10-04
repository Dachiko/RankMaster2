using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RankMaster2.Tray;

/// <summary>
/// The notification-area icon, drawn in code rather than shipped as a <c>.ico</c> (the exe's own
/// icon, <c>App.ico</c>, is a separate file wired in the csproj).
/// <para/>
/// It is the house two-pane mark (plan H § 3.5): two upright rounded panes with a thin outline, drawn
/// for the dark taskbar. The left pane is filled while nothing is open and turns red while a folder
/// is being ranked — the one fact the icon has to give.
/// </summary>
internal static class TrayArt
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <param name="folderOpen">True while a folder is open: the left pane is accent instead of on-ink.</param>
    public static Icon CreateIcon(bool folderOpen)
    {
        // The size the notification area asks for at this DPI (16 at 100 %, 20 at 125 %, ...).
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            // Geometry is the mockup's, on a 20-unit square: panes 7 x 12 at x 2 and x 11, y 4,
            // corner radius 1.2, outline 1.4. The outline never goes under 1.4 px, or it fades out
            // at 16 px.
            var k = size / 20f;
            var stroke = Math.Max(1.4f, 1.4f * k);
            var fill = folderOpen ? House.Accent : House.OnInk;

            using var left = House.RoundedRect(new RectangleF(2 * k, 4 * k, 7 * k, 12 * k), 1.2f * k);
            using var right = House.RoundedRect(new RectangleF(11 * k, 4 * k, 7 * k, 12 * k), 1.2f * k);
            using var fillBrush = new SolidBrush(fill);
            using var leftPen = new Pen(fill, stroke) { LineJoin = LineJoin.Round };
            using var rightPen = new Pen(House.OnInk, stroke) { LineJoin = LineJoin.Round };

            graphics.FillPath(fillBrush, left);
            graphics.DrawPath(leftPen, left);
            graphics.DrawPath(rightPen, right);
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
