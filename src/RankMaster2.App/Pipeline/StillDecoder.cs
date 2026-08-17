using System.IO;
using System.Windows.Media.Imaging;

namespace RankMaster2;

internal static class StillDecoder
{
    public const int PreviewLongEdge = 720;
    public const int MaxLongEdge = 2560;
    public const long PreviewIfLargerThanBytes = 4L * 1024 * 1024;

    public static PreparedFrame Decode(string path, int panelWidth, int panelHeight, bool preview)
    {
        var cap = preview ? PreviewLongEdge : MaxLongEdge;
        panelWidth = Math.Max(panelWidth, 16);
        panelHeight = Math.Max(panelHeight, 16);

        int srcW;
        int srcH;
        var uri = new Uri(path);
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var source = decoder.Frames[0];
        srcW = Math.Max(source.PixelWidth, 1);
        srcH = Math.Max(source.PixelHeight, 1);

        var (dw, dh) = Fit(srcW, srcH, panelWidth, panelHeight, cap);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = uri;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (dw >= dh)
            bmp.DecodePixelWidth = dw;
        else
            bmp.DecodePixelHeight = dh;
        bmp.EndInit();
        bmp.Freeze();

        return new PreparedFrame
        {
            Kind = MediaKind.Still,
            Still = bmp,
            IsPreview = preview
        };
    }

    public static (int Width, int Height) Fit(int srcW, int srcH, int panelW, int panelH, int longEdgeCap)
    {
        var scale = Math.Min(panelW / (double)srcW, panelH / (double)srcH);
        var longEdge = Math.Max(srcW, srcH) * scale;
        if (longEdge > longEdgeCap)
            scale *= longEdgeCap / longEdge;

        var dw = Math.Max(1, (int)Math.Round(srcW * scale));
        var dh = Math.Max(1, (int)Math.Round(srcH * scale));
        return (dw, dh);
    }
}
