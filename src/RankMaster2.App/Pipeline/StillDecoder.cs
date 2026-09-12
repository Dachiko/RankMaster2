using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RankMaster2;

internal static class StillDecoder
{
    public const int PreviewLongEdge = 720;
    public const int MaxLongEdge = 4096;
    public const long PreviewIfLargerThanBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Reads the header for size and orientation, then decodes straight to the size the
    /// pane needs. The decoder never produces more pixels than we are going to draw, so a
    /// 50 MP photo costs its display size instead of its full size.
    /// </summary>
    public static PreparedFrame Decode(string path, int panelWidth, int panelHeight, bool preview)
    {
        var cap = preview ? PreviewLongEdge : MaxLongEdge;
        panelWidth = Math.Max(panelWidth, 16);
        panelHeight = Math.Max(panelHeight, 16);

        var (srcW, srcH, orientation) = ReadFrameInfo(path);

        // Fit belongs in display space (after the EXIF rotation), but the decoder counts
        // pixels on the file's own axes, so map the answer back for sideways orientations.
        var sideways = orientation is >= 5 and <= 8;
        var (dw, dh) = Fit(
            sideways ? srcH : srcW,
            sideways ? srcW : srcH,
            panelWidth,
            panelHeight,
            cap);
        var decodeW = sideways ? dh : dw;
        var decodeH = sideways ? dw : dh;

        var image = new BitmapImage();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            image.BeginInit();
            image.StreamSource = stream;
            // No PreservePixelFormat: that keeps the file's native format and skips the
            // colour-managed conversion, which is what honours the embedded ICC profile.
            image.CreateOptions = BitmapCreateOptions.None;
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Only one axis, so WIC keeps the aspect exactly. Never ask for more than the
            // file has: DecodePixelWidth would happily upscale.
            if (decodeW < srcW)
                image.DecodePixelWidth = decodeW;
            else if (decodeH < srcH)
                image.DecodePixelHeight = decodeH;
            image.EndInit();
        }

        var bitmap = Orient(image, orientation);
        if (!ReferenceEquals(bitmap, image))
        {
            // TransformedBitmap is lazy. Realise it here on the pipeline thread rather than
            // letting the rotation cost land on the UI thread at render time.
            bitmap = new CachedBitmap(bitmap, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
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
        srcW = Math.Max(srcW, 1);
        srcH = Math.Max(srcH, 1);
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

    /// <summary>Header-only read: no pixels are decoded to answer this.</summary>
    private static (int Width, int Height, int Orientation) ReadFrameInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (Math.Max(frame.PixelWidth, 1), Math.Max(frame.PixelHeight, 1), ReadOrientation(frame));
    }

    internal static BitmapSource Orient(BitmapSource source, int orientation)
    {
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
