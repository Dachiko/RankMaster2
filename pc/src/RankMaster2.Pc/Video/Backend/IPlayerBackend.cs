namespace RankMaster2.Pc.Video.Backend;

/// <summary>
/// The thin seam over LibVLCSharp (D-video.md § 2): Initialize, CreatePlayer, Parse, Play, Stop,
/// SetPause, Statistics, Length, the four video callbacks and the two events § 4.5's step table
/// needs. <see cref="LibVlcBackend"/> is the only implementation with a
/// <c>using LibVLCSharp.Shared;</c> anywhere in <c>Video/</c>; <c>FakeBackend</c> (test project) is
/// the other, scriptable one. Everything above this interface — <see cref="VideoSurface"/>, the
/// state machine, <see cref="FailureMapper"/>, <see cref="LoadWatch"/> — is plain C#.
/// </summary>
public interface IPlayerBackend : IDisposable
{
    /// <summary>
    /// Called once, on the engine's worker thread, the first time any surface needs to play. Never
    /// called twice on the same instance. Never throws: failure is reported in the result so
    /// <see cref="VideoEngine"/> can set <see cref="VideoEngineStatus.Failed"/> without a try/catch
    /// at every call site.
    /// </summary>
    EngineInitResult Initialize(string? nativeDirectory, VideoOptions options, Action<string> logSink);

    /// <summary>One per live <see cref="IVideoSurface"/>. Never called before a successful
    /// <see cref="Initialize"/>. <paramref name="callbacks"/> is the surface itself — the backend
    /// wires VLC's native callbacks to it once, at creation, not per <c>Play</c>.</summary>
    IBackendPlayer CreatePlayer(IPlayerCallbacks callbacks);
}

public readonly record struct EngineInitResult(bool Success, string? FailureMessage)
{
    public static EngineInitResult Ok() => new(true, null);
    public static EngineInitResult Failed(string message) => new(false, message);
}

public enum ParseOutcome { Ok, Failed, Timeout }

/// <summary>The result of <see cref="IBackendPlayer.Parse"/> — step 2/3 of § 4.5's table in one
/// value: whether the demux succeeded, and if so, whether it found a video track and what its
/// resolution/codec are (recorded into <see cref="VideoStats"/> before a decoder exists).</summary>
public readonly record struct ParseResult(ParseOutcome Outcome, bool HasVideoTrack, int Width, int Height, string Codec)
{
    public static ParseResult Failed() => new(ParseOutcome.Failed, false, 0, 0, "");
    public static ParseResult TimedOut() => new(ParseOutcome.Timeout, false, 0, 0, "");
    public static ParseResult NoVideoTrack() => new(ParseOutcome.Ok, false, 0, 0, "");
    public static ParseResult HasVideo(int width, int height, string codec) => new(ParseOutcome.Ok, true, width, height, codec);
}

/// <summary>LibVLC's <c>Media.Statistics</c>, the two counters <see cref="LoadWatch"/> needs.</summary>
public readonly record struct BackendStatistics(long DecodedFrames, long LostFrames);

/// <summary>
/// One per surface, for the surface's lifetime (D-video.md § 4.4's ownership table). Every member
/// that reaches native code is called only from the engine's single worker thread — the interface
/// does not enforce that; <see cref="VideoEngine"/> and <see cref="VideoSurface"/> do, by construction.
/// </summary>
public interface IBackendPlayer : IDisposable
{
    /// <summary>§ 4.5 step 2/3: demux-only parse with a timeout. The parsed media is retained
    /// internally for the <see cref="Play"/> that follows.</summary>
    ParseResult Parse(string path, TimeSpan timeout);

    /// <summary>§ 4.5 step 4: starts playback of the media the immediately preceding
    /// <see cref="Parse"/> parsed. <paramref name="generation"/> is captured and threaded through
    /// every later callback/event this Play triggers (D-video.md § 4.4 rule 2). Returns false if
    /// libvlc's own <c>Play()</c> returned false.</summary>
    bool Play(VideoOptions options, int generation);

    /// <summary>§ 4.5 step 10, the belt half of looping: <c>Position = 0; Play()</c>, posted to the
    /// worker exactly like the old app — never called from inside a callback.</summary>
    void Restart();

    /// <summary>Stops the player and releases its media (D-video.md § 4.4 rule "old Stop() order":
    /// <c>player.Media = null</c> then <c>media.Dispose()</c>). Safe to call in any state, any number
    /// of times.</summary>
    void Stop();

    void SetPause(bool paused);

    BackendStatistics Statistics();

    /// <summary>The clip's reported length, once known (zero before then).</summary>
    TimeSpan Length { get; }
}

/// <summary>
/// The surface's side of the four video callbacks and two events, implemented by
/// <see cref="VideoSurface"/> and invoked by the backend. Every method carries the generation that
/// was current when the triggering <see cref="IBackendPlayer.Play"/> was called, so a stale callback
/// from a superseded generation is a no-op at the call site (D-video.md § 4.4 rule 2) instead of a
/// data race.
/// </summary>
public interface IPlayerCallbacks
{
    /// <summary>OnFormat: given the source size VLC parsed, return the pane-fitted (width, height,
    /// pitch, lines) to allocate for and to hand back to VLC as the requested RV32 format.</summary>
    (int Width, int Height, int Pitch, int Lines) OnFormat(int sourceWidth, int sourceHeight, int generation);

    /// <summary>OnCleanup.</summary>
    void OnCleanup(int generation);

    /// <summary>OnLock: the native pointer VLC should decode into.</summary>
    IntPtr OnLock(int generation);

    /// <summary>OnDisplay: the frame at that pointer is complete.</summary>
    void OnDisplay(int generation);

    /// <summary>EncounteredError.</summary>
    void OnEncounteredError(int generation);

    /// <summary>EndReached.</summary>
    void OnEndReached(int generation);
}
