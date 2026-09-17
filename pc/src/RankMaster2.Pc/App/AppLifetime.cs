namespace RankMaster2.Pc.App;

using RankMaster2.Pc.Link;

/// <summary>
/// A-startup-and-shell.md § 3.4. Esc kills the process (SPEC.md § Keys); this is what runs first,
/// capped at 500 ms, before it does. Split from <see cref="QuitNow"/> so a test can prove the cap
/// without ending the test host (§ 8 tests 10, 12).
/// </summary>
public sealed class AppLifetime
{
    private readonly ISessionLink _link;
    private readonly string? _startupLogPath;

    public AppLifetime(ISessionLink link, string? startupLogPath = null)
    {
        _link = link;
        _startupLogPath = startupLogPath;
    }

    /// <summary>Closes the session with a 500 ms cap (part B's DELETE /session; 404 is fine; a
    /// timeout is fine) and flushes the startup log. Never throws.</summary>
    public async Task PrepareQuitAsync()
    {
        StartupClock.Mark("quit");
        try { StartupClock.Flush(_startupLogPath); }
        catch { /* diagnostic only */ }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var close = SafeCloseAsync(cts.Token);
        await Task.WhenAny(close, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
    }

    private async Task SafeCloseAsync(CancellationToken ct)
    {
        try
        {
            await _link.CloseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A21: Esc ends the process regardless of what happens here — SPEC.md says Esc quits
            // immediately, and nothing is written on close (SERVER_SPEC.md § 10.4) so there is
            // nothing to roll back. The old blanket `catch { }` hid a genuinely failing close as
            // readily as an expected timeout; this is the only debugging channel from the owner's
            // PC, so the failure goes there instead of nowhere.
            try { StartupClock.AppendNote($"close_failed {ex.GetType().Name}: {ex.Message}", _startupLogPath); }
            catch { /* the process is ending either way */ }
        }
    }

    /// <summary>Runs <see cref="PrepareQuitAsync"/> then ends the process. Not used by tests (they
    /// call <see cref="PrepareQuitAsync"/> directly so the test host survives).</summary>
    public async Task QuitNow()
    {
        await PrepareQuitAsync().ConfigureAwait(false);
        Environment.Exit(0);
    }
}
