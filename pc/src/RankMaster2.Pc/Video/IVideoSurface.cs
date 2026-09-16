namespace RankMaster2.Pc.Video;

using Avalonia;                       // PixelSize
using Avalonia.Media.Imaging;         // Bitmap

/// <summary>One per process. Its constructor touches nothing native.</summary>
public interface IVideoSurfaceFactory
{
    /// <summary>
    /// Begins LibVLC initialisation if it has not begun; returns the same task on every later call.
    /// Never throws: a failed engine is reported as <see cref="VideoEngineStatus.Failed"/>.
    /// </summary>
    Task<VideoEngineStatus> WarmUpAsync(CancellationToken ct = default);

    VideoEngineStatus EngineStatus { get; }
    /// <summary>Non-null iff <see cref="EngineStatus"/> is Failed. One sentence, for the screen.</summary>
    string? EngineFailure { get; }

    /// <summary>
    /// One per pane. Implicitly calls <see cref="WarmUpAsync"/>. Throws InvalidOperationException
    /// when two surfaces are already alive — there is never a third player.
    /// </summary>
    IVideoSurface Create(string paneName);

    int LiveSurfaces { get; }

    /// <summary>Raised on the UI thread when EngineStatus changes.</summary>
    event Action<VideoEngineStatus>? EngineStatusChanged;

    /// <summary>The last LibVLC log lines and the live counters, for the crash file and the probe.</summary>
    string DiagnosticsDump();
}

public interface IVideoSurface : IDisposable
{
    string PaneName { get; }
    VideoSurfaceState State { get; }

    /// <summary>The path given to the last Play, until StopAsync or the next Play.</summary>
    string? Path { get; }

    /// <summary>
    /// The pane-fitted frame. Null until the first frame arrives and again after StopAsync.
    /// The same object is written in place for every later frame; E redraws on FrameChanged.
    /// </summary>
    Bitmap? Frame { get; }
    PixelSize FrameSize { get; }

    /// <summary>Non-null iff State is Failed.</summary>
    VideoFailure? Failure { get; }
    VideoStats Stats { get; }

    /// <summary>
    /// Device pixels available to this pane. Applied at the next Play; a change while playing
    /// takes effect only when the next file opens (the window is fullscreen and does not resize).
    /// </summary>
    void SetPaneSize(PixelSize pixels);

    /// <summary>
    /// Opens and plays <paramref name="path"/> muted and looping. Returns at once; progress arrives
    /// through StateChanged. Calling it with the path already playing is a no-op. Calling it with a
    /// different path stops the current one first.
    /// </summary>
    void Play(string path);

    /// <summary>
    /// Stops playback, detaches the media and releases the file. The task completes when the file
    /// can be opened with FileShare.None (or after 2 s, with a logged warning). State becomes Idle
    /// and Frame becomes null. Safe to call in any state, any number of times.
    /// </summary>
    Task StopAsync();

    /// <summary>UI thread. Fires on every transition, including into Failed.</summary>
    event Action<IVideoSurface>? StateChanged;

    /// <summary>UI thread. At most once per UI-thread turn; E calls InvalidateVisual on its Image.</summary>
    event Action<IVideoSurface>? FrameChanged;
}

public enum VideoEngineStatus { Asleep, Starting, Ready, Failed }

public enum VideoSurfaceState
{
    Idle,      // nothing requested, or stopped
    Opening,   // Play called, no frame yet — E shows the spinner
    Playing,   // frames arriving
    Holding,   // paused on its last frame by the engine's alternate mode — E shows the frame, optionally a marker
    Failed     // see Failure
}

public enum VideoFailureKind
{
    EngineUnavailable,  // LibVLC could not initialise; every surface reports this
    Missing,            // the file is not on disk (E: "file is gone"; discard → drop_missing)
    Unreadable,         // LibVLC could not parse the container
    NoVideoTrack,       // parsed, but nothing to show (audio-only, data-only)
    DecodeFailed,       // VLC raised EncounteredError, or reached the end before any frame
    NoFrameInTime       // opened, no error, no frame within VideoOptions.FirstFrameTimeout
}

/// <param name="Message">One sentence for the screen, no VLC jargon.</param>
/// <param name="Detail">The last LibVLC log lines, for the crash file and the owner's report.</param>
public sealed record VideoFailure(VideoFailureKind Kind, string Message, string? Detail);

public readonly record struct VideoStats(
    int SourceWidth, int SourceHeight,   // from the parsed track, 0 until parsed
    string Codec,                        // fourcc as text, "" until parsed
    long DecodedFrames, long LostFrames, // from LibVLC statistics, cumulative for this Play
    double LostFrameRate,                // lost / (lost + displayed) over the LoadWatch window, 0..1
    bool Alternating,                    // the engine has put the pair into alternate mode
    TimeSpan TimeToFirstFrame);          // zero until Playing
