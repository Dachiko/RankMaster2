namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Reflection.PortableExecutable;

/// <summary>
/// A-startup-and-shell.md § 4.4: is <paramref name="dll"/> really a ReadyToRun image? Opens it with
/// <see cref="PEReader"/> and reports whether the CLI header's <c>ManagedNativeHeader</c> directory
/// is non-empty — that is the definition of an R2R image. Used by <c>publish.sh</c>'s shape check.
/// </summary>
internal static class IsR2RCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("is-r2r: needs <dll>");
            return 1;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"is-r2r: '{path}' does not exist");
            return 1;
        }

        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);

        var isR2R = false;
        if (reader.HasMetadata)
        {
            var headers = reader.PEHeaders;
            var corHeader = headers.CorHeader;
            if (corHeader is not null)
            {
                var dir = corHeader.ManagedNativeHeaderDirectory;
                isR2R = dir.Size > 0;
            }
        }

        Console.WriteLine($"{Path.GetFileName(path)} r2r={(isR2R ? "yes" : "no")}");
        return isR2R ? 0 : 1;
    }
}
