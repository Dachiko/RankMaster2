namespace RankMaster2.Pc.App;

using RankMaster2.Pc.Video;

/// <summary>
/// A-startup-and-shell.md § 3.3. The single place that decides to wake part D's video engine
/// early, and that runs the index check before it. <see cref="WarmUpAsync"/> is idempotent: the
/// first caller starts the work, every later caller (§ 3.2's decorator, a late snapshot, D's own
/// "when it sees a video snapshot" call) awaits the same <see cref="Task"/>.
/// </summary>
public sealed class VideoEngineGate
{
    private readonly IVideoSurfaceFactory _engine;
    private readonly string? _nativeDir;
    private readonly string? _libvlcVersion;
    private readonly Func<string, LibVlcIndexResult> _build;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Task? _warmUp;

    /// <param name="engine">D's <c>VideoEngine</c> in production, a fake in tests.</param>
    /// <param name="nativeDir">Overrides <see cref="LibVlcLayout.NativeDir"/> — a test's temp
    /// plugin directory, or null (default) to use the real layout, which is itself null off
    /// Windows.</param>
    /// <param name="libvlcVersion">
    /// AUDIT2.md § 3.6: overrides the stamp's version tag — a test's fixed value, for a
    /// deterministic comparison. Null (the default, and what production always passes) means "ask
    /// <see cref="LibVlcIndex.DescribeLibVlcBuild"/>", which fingerprints <c>libvlc.dll</c> and
    /// <c>libvlccore.dll</c> by size and write time rather than guessing at the string the runtime
    /// would report — confirmed on the real 3.0.21 binary to carry a codename
    /// (<c>"3.0.21 Vetinari"</c>) that a hard-coded <c>"3.0.21"</c> here never matched, so the index
    /// this class exists to reuse was being silently rebuilt before every first video, every launch.
    /// </param>
    /// <param name="build">Overrides <see cref="LibVlcIndex.Build"/> — the § 8 test 6 stub.</param>
    public VideoEngineGate(
        IVideoSurfaceFactory engine,
        string? nativeDir = null,
        string? libvlcVersion = null,
        Func<string, LibVlcIndexResult>? build = null,
        Action<string>? log = null)
    {
        _engine = engine;
        _nativeDir = nativeDir ?? LibVlcLayout.NativeDir;
        _libvlcVersion = libvlcVersion;
        _build = build ?? LibVlcIndex.Build;
        _log = log ?? (_ => { });
    }

    public bool IsAwake => _engine.EngineStatus is VideoEngineStatus.Starting or VideoEngineStatus.Ready;

    public Task WarmUpAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _warmUp ??= WarmUpCoreAsync(ct);
            return _warmUp;
        }
    }

    private async Task WarmUpCoreAsync(CancellationToken ct)
    {
        StartupClock.Mark("vlc_wake_begin");
        EnsureIndexCurrent();
        await _engine.WarmUpAsync(ct).ConfigureAwait(false);
        StartupClock.Mark("vlc_wake_end");
    }

    private void EnsureIndexCurrent()
    {
        if (_nativeDir is null)
            return; // no Windows-style libvlc dir here (Linux, or a test with none) — nothing to index

        var pluginsDir = Path.Combine(_nativeDir, "plugins");
        if (!Directory.Exists(pluginsDir))
            return;

        var indexPath = Path.Combine(pluginsDir, LibVlcIndex.PluginsDatName);
        var stampPath = Path.Combine(pluginsDir, LibVlcIndex.StampName);
        var versionTag = _libvlcVersion ?? LibVlcIndex.DescribeLibVlcBuild(_nativeDir);

        bool current;
        try { current = LibVlcIndex.IsCurrent(pluginsDir, indexPath, stampPath, versionTag); }
        catch { current = false; }

        if (current)
            return;

        if (!CanWrite(pluginsDir))
        {
            _log("vlc_index_unwritable");
            return; // § 3.3: warm the engine anyway; VLC scans the manifest's files the slow way
        }

        try
        {
            var result = _build(_nativeDir);
            if (result.ExitCode == 0)
                StartupClock.Mark("vlc_index_rebuilt");
            else
                _log($"vlc_index_rebuild_failed: {result.Message}");
        }
        catch (Exception ex)
        {
            _log($"vlc_index_rebuild_failed: {ex.Message}");
        }
    }

    private static bool CanWrite(string dir)
    {
        var probe = Path.Combine(dir, $".rm2-write-check-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(probe); } catch { /* best effort */ }
        }
    }
}
