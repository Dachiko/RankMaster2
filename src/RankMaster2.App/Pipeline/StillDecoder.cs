using System.IO;
using System.Windows.Media;
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
        var source = ApplyExifOrientation(decoder.Frames[0]);
        var srcW = Math.Max(source.PixelWidth, 1);
        var srcH = Math.Max(source.PixelHeight, 1);
        var (dw, dh) = Fit(srcW, srcH, panelWidth, panelHeight, cap);

        BitmapSource bitmap = source;
        if (dw != srcW || dh != srcH)
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(
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

    internal static BitmapSource ApplyExifOrientation(BitmapSource source)
    {
        var orientation = ReadOrientation(source);
        if (orientation <= 1)
            return source;

        var group = new TransformGroup();
        switch (orientation)
        {
            case 2:
                group.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 3:
                group.Children.Add(new RotateTransform(180));
                break;
            case 4:
                group.Children.Add(new ScaleTransform(1, -1));
                break;
            case 5:
                group.Children.Add(new RotateTransform(90));
                group.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 6:
                group.Children.Add(new RotateTransform(90));
                break;
            case 7:
                group.Children.Add(new RotateTransform(270));
                group.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 8:
                group.Children.Add(new RotateTransform(270));
                break;
            default:
                return source;
        }

        var oriented = new TransformedBitmap(source, group);
        oriented.Freeze();
        return oriented;
    }

    private static int ReadOrientation(BitmapSource source)
    {
        try
        {
            if (source.Metadata is not BitmapMetadata meta)
                return 1;
            var value = meta.GetQuery("/app1/ifd/{ushort=274}");
            return value switch
            {
                ushort u => u,
                int i => i,
                _ => 1
            };
        }
        catch
        {
            return 1;
        }
    }
}
