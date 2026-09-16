namespace RankMaster2.Pc.Video;

/// <summary>The fallback ladder of D-video.md § 4.3, as pure state: lost-frame samples in, a mode out.
/// Driven by <see cref="VideoEngine"/> once a second while two surfaces play; has no idea what a
/// <c>MediaPlayer</c> is, so it is exercised directly and fast by § 6.1 test 7.</summary>
public enum LoadWatchMode
{
    /// <summary>Both surfaces play at once — the default.</summary>
    Both,

    /// <summary>Alternate mode, left surface active (playing), right held (paused on its last frame).</summary>
    AlternateLeftActive,

    /// <summary>Alternate mode, right surface active, left held.</summary>
    AlternateRightActive
}

public sealed class LoadWatch
{
    private readonly VideoOptions _options;
    private int _consecutiveOver;
    private DateTimeOffset _lastSwap;

    public LoadWatch(VideoOptions options) => _options = options;

    public LoadWatchMode Mode { get; private set; } = LoadWatchMode.Both;

    public bool IsAlternating => Mode != LoadWatchMode.Both;

    /// <summary>
    /// Call once per <c>LoadWatchInterval</c> while both surfaces are Playing/Holding.
    /// <paramref name="leftRate"/>/<paramref name="rightRate"/> are each surface's LostFrameRate;
    /// <paramref name="clipLength"/> is the longer of the two clips' lengths (zero if unknown, in
    /// which case <c>MinSwapInterval</c> alone governs); <paramref name="forceAlternate"/> is
    /// <c>VideoOptions.ForceAlternate</c>, for tests and rm2vidprobe.
    /// </summary>
    public LoadWatchMode Sample(double leftRate, double rightRate, TimeSpan clipLength, DateTimeOffset now, bool forceAlternate = false)
    {
        if (Mode == LoadWatchMode.Both)
        {
            if (forceAlternate || leftRate > _options.AlternateThreshold || rightRate > _options.AlternateThreshold)
                _consecutiveOver++;
            else
                _consecutiveOver = 0;

            if (_consecutiveOver >= _options.AlternateThresholdSamples)
            {
                // The phone's option B: alternate, left starts active. No gesture is spent, and this
                // is sticky for the session (D-video.md § 4.3) — only Reset() (LiveSurfaces == 0)
                // clears it, not a later good sample.
                Mode = LoadWatchMode.AlternateLeftActive;
                _lastSwap = now;
                _consecutiveOver = 0;
            }

            return Mode;
        }

        var interval = clipLength > _options.MinSwapInterval ? clipLength : _options.MinSwapInterval;
        if (now - _lastSwap >= interval)
        {
            Mode = Mode == LoadWatchMode.AlternateLeftActive
                ? LoadWatchMode.AlternateRightActive
                : LoadWatchMode.AlternateLeftActive;
            _lastSwap = now;
        }

        return Mode;
    }

    /// <summary>LiveSurfaces dropped to zero (the compare view was left): the ladder resets for
    /// whatever plays next.</summary>
    public void Reset()
    {
        Mode = LoadWatchMode.Both;
        _consecutiveOver = 0;
        _lastSwap = default;
    }
}
