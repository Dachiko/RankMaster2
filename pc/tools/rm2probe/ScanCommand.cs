namespace RankMaster2.Pc.Tools.Rm2Probe;

using System.Diagnostics;
using RankMaster2.Catalog;

/// <summary>
/// A-startup-and-shell.md § 6.4: <c>JsonCatalog.Scan</c> ×3, read-only. <c>Scan</c> is read-only —
/// <c>JsonCatalog.Save</c> is never called here or anywhere else in this tool; nothing this command
/// does writes inside the owner's media folders.
/// </summary>
internal static class ScanCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("scan: needs <folder>");
            return 1;
        }

        var folder = args[0];
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"scan: skipped: '{folder}' does not exist");
            return 0;
        }

        var dbPath = Path.Combine(folder, JsonCatalog.FileName);
        var jsonKb = File.Exists(dbPath) ? new FileInfo(dbPath).Length / 1024.0 : 0;

        var timings = new List<long>();
        var fileCount = 0;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var records = new JsonCatalog().Scan(folder);
                fileCount = records.Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"scan: skipped: {ex.Message}");
                return 0;
            }
            sw.Stop();
            timings.Add(sw.ElapsedMilliseconds);
        }

        Console.WriteLine($"files={fileCount} json={jsonKb:F0}KB scan={string.Join('/', timings)}ms");
        return 0;
    }
}
