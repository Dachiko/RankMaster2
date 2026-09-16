namespace RankMaster2.Pc.Stills;

/// <summary>
/// Plan section 3.8 step 3: the exclusive-open probe <see cref="StillSource.ReleaseAsync"/> runs
/// after it has dropped its own frame reference and waited for any in-flight decode. Rewritten
/// here in ~15 lines rather than referenced from <c>RankMaster2.Catalog.FileOps.WaitUntilUnlocked</c>
/// so this part does not depend on Catalog (plan section 1, "Dependencies").
/// <para/>
/// Only meaningful on Windows -- Linux does not enforce sharing, so this always succeeds quickly
/// there once nothing in THIS process holds the file (plan section 7).
/// </summary>
internal static class HandleCheck
{
    private const int Attempts = 40;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50); // 40 * 50ms = 2s

    public static async Task<bool> CanOpenExclusivelyAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
                return true; // nothing to release -- already gone

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                // Still locked by something else. Wait and retry, unless this was the last attempt.
            }
            catch (UnauthorizedAccessException)
            {
                // Could be a genuine permission problem or (on some platforms) a transient sharing
                // error reported this way. Treat it the same as "still locked" and keep retrying;
                // the caller's 2 s ceiling is the backstop either way.
            }

            if (attempt < Attempts - 1)
                await Task.Delay(Interval, ct).ConfigureAwait(false);
        }

        return false;
    }
}
