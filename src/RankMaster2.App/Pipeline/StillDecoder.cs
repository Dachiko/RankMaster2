using System.IO;
using System.Windows.Media.Imaging;

namespace RankMaster2;

internal static class StillDecoder
{
    public const int PreviewLongEdge = 720;
    public const int MaxLongEdge = 4096;
    public const long PreviewIfLargerThanBytes = 4L * 1024 * 1024;

    public static PreparedFrame Decode(string path, int panelWidth, int panelHeight, bool preview)
    {
        var cap = preview ? PreviewLongEdge : MaxLongEdge;
        panelWidth = Math.Max(panelWidth, 16);
        panelHeight = Math.Max(panelHeight, 16);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        var srcW = Math.Max(source.PixelWidth, 1);
        var srcH = Math.Max(source.PixelHeight, 1);
        var (dw, dh) = Fit(srcW, srcH, panelWidth, panelHeight, cap);

        BitmapSource bitmap = source;
        if (dw != srcW || dh != srcH)
        {
            var scaled = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(
                dw / (double)srcW, dh / (double)srcH));
            scaled.Freeze();
            bitmap = scaled;
        }

        if (!bitmap.IsFrozen)
            bitmap.Freeze();

        return new PreparedFrame
        {
            Kind = MediaKind.Still,
            Still = bitmap,
            IsPreview = preview
        };
    }

    public static (int Width, int Height) Fit(int srcW, int srcH, int panelW, int panelH, int longEdgeCap)
    {
        var scale = Math.Min(panelW / (double)srcW, panelH / (double)srcH);
        if (scale > 1)
            scale = 1;

        var longEdge = Math.Max(srcW, srcH) * scale;
        if (longEdge > longEdgeCap)
            scale *= longEdgeCap / longEdge;

        var dw = Math.Max(1, (int)Math.Round(srcW * scale));
        var dh = Math.Max(1, (int)Math.Round(srcH * scale));
        return (dw, dh);
    }
}
