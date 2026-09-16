using System.Runtime.InteropServices;
using RankMaster2.Pc.Stills;

namespace RankMaster2.Pc.Stills.Tests.Fixtures;

/// <summary>Reads BGRA8888 pixels straight out of a StillFrame's native buffer, for pixel-asserted tests (plan section 3.2: colour and orientation are silent failures, so they are checked on pixels, not status).</summary>
internal static class PixelReader
{
    public static (byte B, byte G, byte R, byte A) At(StillFrame frame, int x, int y)
    {
        var offset = y * frame.RowBytes + x * 4;
        var ptr = frame.Pixels;
        return (
            Marshal.ReadByte(ptr, offset),
            Marshal.ReadByte(ptr, offset + 1),
            Marshal.ReadByte(ptr, offset + 2),
            Marshal.ReadByte(ptr, offset + 3));
    }

    public static string HexRgb(StillFrame frame, int x, int y)
    {
        var (b, g, r, _) = At(frame, x, y);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    /// <summary>The server's MeanDifference, on a 9x9 grid, adapted to read StillFrame buffers directly instead of SKBitmap.</summary>
    public static double MeanDifference(StillFrame a, StillFrame b)
    {
        const int steps = 10;
        double total = 0;
        var count = 0;

        for (var iy = 1; iy < steps; iy++)
        {
            for (var ix = 1; ix < steps; ix++)
            {
                var pa = At(a, a.Width * ix / steps, a.Height * iy / steps);
                var pb = At(b, b.Width * ix / steps, b.Height * iy / steps);
                total += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                count += 3;
            }
        }

        return count == 0 ? 0 : total / count;
    }
}
