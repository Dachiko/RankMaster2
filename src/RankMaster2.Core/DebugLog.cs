namespace RankMaster2;

/// <summary>
/// Temporary debug logger. Delete this file (and all DebugLog.Write calls) when video load is stable.
/// Disk ranking writes are gated by <see cref="DiskSavesEnabled"/>.
/// </summary>
public static class DebugLog
{
    // App constructor sets this false so ranking JSON is not rewritten while debugging video load.
    public static bool DiskSavesEnabled = true;

    public static readonly string FilePath = @"C:\Utils\rank master 2\debug.log";

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            File.AppendAllText(FilePath, line);
        }
        catch
        {
            // never break the app for logging
        }
    }
}
