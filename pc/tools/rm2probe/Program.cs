namespace RankMaster2.Pc.Tools.Rm2Probe;

/// <summary>A-startup-and-shell.md § 6.4. One console, both jobs: the measurement kit's LibVLC
/// probe, and the shape check <c>publish.sh</c> and the § 8 test suite use (is-r2r). Every
/// subcommand prints one block of plain text and exits 0/1; nothing it does writes inside the
/// owner's media folders — <c>scan</c> only ever calls <c>JsonCatalog.Scan</c>, never
/// <c>JsonCatalog.Save</c>.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "vlc-init" => VlcInitCommand.Run(args[1..]),
                "vlc-cache" => VlcCacheCommand.Run(args[1..]),
                "play" => PlayCommand.Run(args[1..]),
                "scan" => ScanCommand.Run(args[1..]),
                "decode" => DecodeCommand.Run(args[1..]),
                "is-r2r" => IsR2RCommand.Run(args[1..]),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rm2probe: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"rm2probe: unknown subcommand '{cmd}'");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            usage: rm2probe <subcommand> [args]
              vlc-init  <libvlcDir> [--runs N] [--once]
              vlc-cache <libvlcDir> [--copy-to <dir>]
              play      <file-or-dir> [--libvlc <dir>] [--seconds N] [--min-frames N] [--report <file>]
              scan      <folder>
              decode    <file-or-folder> [--width N]
              is-r2r    <dll>
            """);
    }

    internal static string? OptionValue(string[] args, string name) =>
        Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    internal static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
