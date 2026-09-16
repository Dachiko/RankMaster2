namespace RankMaster2.Pc.Video;

/// <summary>Every way D-video.md § 4.5's step table can end, one per row of the table below the
/// header. Pure: <see cref="FailureMapper.Map"/> turns a signal plus the last log lines into the
/// <see cref="VideoFailure"/> E shows — no VLC jargon in <c>Message</c>, VLC's own words only in
/// <c>Detail</c>.</summary>
public enum FailureSignal
{
    /// <summary>Step 1: File.Exists false. No backend call was made.</summary>
    FileMissing,

    /// <summary>Step 2: Parse returned Failed.</summary>
    ParseFailed,

    /// <summary>Step 2: Parse returned Timeout.</summary>
    ParseTimedOut,

    /// <summary>Step 3: parsed, but Tracks has no Video entry.</summary>
    NoVideoTrack,

    /// <summary>Step 4: player.Play(media) returned false.</summary>
    PlayReturnedFalse,

    /// <summary>Step 5: EncounteredError before the first frame.</summary>
    EncounteredErrorBeforeFirstFrame,

    /// <summary>Step 9: EncounteredError while Playing/Holding — Frame is kept; only State changes.</summary>
    EncounteredErrorWhilePlaying,

    /// <summary>Step 6: EndReached before the first frame.</summary>
    EndReachedBeforeFirstFrame,

    /// <summary>Step 7: no OnDisplay within VideoOptions.FirstFrameTimeout.</summary>
    NoFrameWithinTimeout
}

public static class FailureMapper
{
    public static VideoFailure Map(FailureSignal signal, IReadOnlyList<string>? logLines = null, TimeSpan? timeout = null)
    {
        var detail = logLines is null || logLines.Count == 0 ? null : string.Join('\n', logLines);

        return signal switch
        {
            FailureSignal.FileMissing =>
                new VideoFailure(VideoFailureKind.Missing, "The file is gone.", null),

            FailureSignal.ParseFailed or FailureSignal.ParseTimedOut =>
                new VideoFailure(VideoFailureKind.Unreadable,
                    "This file cannot be played: its container could not be read.", detail),

            FailureSignal.NoVideoTrack =>
                new VideoFailure(VideoFailureKind.NoVideoTrack,
                    "This file has no video to show.", detail),

            FailureSignal.PlayReturnedFalse or FailureSignal.EncounteredErrorBeforeFirstFrame
                or FailureSignal.EncounteredErrorWhilePlaying =>
                new VideoFailure(VideoFailureKind.DecodeFailed,
                    "This file cannot be played.", detail),

            FailureSignal.EndReachedBeforeFirstFrame =>
                new VideoFailure(VideoFailureKind.DecodeFailed,
                    "This file cannot be played: the file ends before its first frame.", detail),

            FailureSignal.NoFrameWithinTimeout =>
                new VideoFailure(VideoFailureKind.NoFrameInTime,
                    $"This file cannot be played: no frame arrived within {(timeout ?? TimeSpan.Zero).TotalSeconds:0}s.",
                    detail),

            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "unmapped failure signal")
        };
    }
}
