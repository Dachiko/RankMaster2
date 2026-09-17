namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Diagnostics;
using SkiaSharp;

/// <summary>A-startup-and-shell.md § 6.4: SkiaSharp decode-at-size of the given still, or of the
/// largest still in the folder, three times.</summary>
internal static class DecodeCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("decode: needs <file-or-folder>");
            return 1;
        }

        var target = args[0];
        var width = int.TryParse(Program.OptionValue(args, "--width"), out var w) ? w : 2160;

        string? file = null;
        if (File.Exists(target))
        {
            file = target;
        }
        else if (Directory.Exists(target))
        {
            file = Directory.EnumerateFiles(target)
                .Where(f => RankMaster2.MediaExtensions.KindOf(Path.GetFileName(f)) == RankMaster2.MediaKind.Still)
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.Length)
                .FirstOrDefault()
                ?.FullName;
        }

        if (file is null)
        {
            Console.WriteLine($"decode: skipped: no still found at '{target}'");
            return 0;
        }

        using var codecCheck = SKCodec.Create(file);
        if (codecCheck is null)
        {
            Console.WriteLine($"decode: skipped: '{file}' is not a decodable image here");
            return 0;
        }

        var info = codecCheck.Info;
        var megapixels = info.Width * (double)info.Height / 1_000_000.0;

        var timings = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            using var codec = SKCodec.Create(file);
            var srcInfo = codec!.Info;
            var scale = Math.Min(1.0, width / (double)Math.Max(srcInfo.Width, srcInfo.Height));
            var targetInfo = new SKImageInfo(
                Math.Max(1, (int)(srcInfo.Width * scale)),
                Math.Max(1, (int)(srcInfo.Height * scale)));
            using var bitmap = new SKBitmap(targetInfo);
            codec.GetPixels(targetInfo, bitmap.GetPixels());
            sw.Stop();
            timings.Add(sw.ElapsedMilliseconds);
        }

        Console.WriteLine($"{Path.GetFileName(file)} {megapixels:F1}MP decode@{width}={string.Join('/', timings)}ms");
        return 0;
    }
}
