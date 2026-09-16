namespace RankMaster2.Pc.Video;

/// <summary>Every tunable in one place, with the defaults D-video.md § 5.2 sets. Nothing in
/// <c>Video/</c> reads a magic number that is not on this record.</summary>
public sealed record VideoOptions
{
    /// <summary>§ 4.5 step 7: no frame within this long after Play() succeeds → Failed(NoFrameInTime).</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>§ 4.5 step 2: the demux-only Parse's timeout.</summary>
    public TimeSpan ParseTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>§ 4.4 rule 4: StopAsync polls this many times for FileShare.None...</summary>
    public int ReleaseWaitAttempts { get; init; } = 40;

    /// <summary>...at this interval (40 x 50ms = 2s, then it completes anyway and logs).</summary>
    public TimeSpan ReleaseWaitInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>§ 4.3: how often the engine samples LostFrameRate while two surfaces play.</summary>
    public TimeSpan LoadWatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>§ 4.3: the LostFrameRate that, sustained for AlternateThresholdSamples samples, drops
    /// the pair into alternate mode.</summary>
    public double AlternateThreshold { get; init; } = 0.25;

    /// <summary>§ 4.3: consecutive samples over AlternateThreshold before alternate mode engages.</summary>
    public int AlternateThresholdSamples { get; init; } = 3;

    /// <summary>§ 4.3: roles swap every max(clip length, this).</summary>
    public TimeSpan MinSwapInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>0 = auto (one thread per logical CPU). Non-zero adds :dav1d-thread-frames=N per media
    /// so rm2vidprobe can try a cap on the owner's PC without a rebuild.</summary>
    public int Dav1dThreads { get; init; }

    /// <summary>For tests and rm2vidprobe: forces the engine straight into alternate mode regardless
    /// of measured LostFrameRate.</summary>
    public bool ForceAlternate { get; init; }

    /// <summary>The last-N-lines ring VideoLog keeps.</summary>
    public int LogRingSize { get; init; } = 100;

    /// <summary>§ 1: "how many players may exist" — two, the factory throws on a third.</summary>
    public int MaxLiveSurfaces { get; init; } = 2;
}
