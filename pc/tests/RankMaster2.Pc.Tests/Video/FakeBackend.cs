namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using RankMaster2.Pc.Video.Backend;

/// <summary>
/// Scriptable, in-memory <see cref="IPlayerBackend"/> for the pure test suite (D-video.md § 6.1) — no
/// <c>LibVLCSharp</c> anywhere in this file, per D-video.md § 2's layering rule and the plan's own
/// note that "FakeBackend lives in the test project, not here — listed so nobody puts a fake in src".
/// One instance is normally shared by both surfaces in a test, mirroring <see cref="VideoEngine"/>'s
/// real one-backend-many-players shape.
/// </summary>
public sealed class FakeBackend : IPlayerBackend
{
    public int InitializeCalls { get; private set; }
    public bool InitializeShouldFail { get; set; }
    public string InitializeFailureMessage { get; set; } = "fake failure";
    public bool Disposed { get; private set; }
    public List<string> LoggedLines { get; } = new();

    public readonly List<FakePlayer> Players = new();

    /// <summary>Set by a test before Play() to script the next FakePlayer's Parse/Play outcome —
    /// e.g. <c>backend.CreatePlayerOverride = p =&gt; p.NextParseResult = ParseResult.Failed();</c>.
    /// Runs at CreatePlayer time, before the surface's worker action calls Parse.</summary>
    public Action<FakePlayer>? CreatePlayerOverride { get; set; }

    public EngineInitResult Initialize(string? nativeDirectory, VideoOptions options, Action<string> logSink)
    {
        InitializeCalls++;
        var line = $"[fake] Initialize nativeDirectory={nativeDirectory}";
        LoggedLines.Add(line);
        logSink(line);
        return InitializeShouldFail ? EngineInitResult.Failed(InitializeFailureMessage) : EngineInitResult.Ok();
    }

    public IBackendPlayer CreatePlayer(IPlayerCallbacks callbacks)
    {
        var player = new FakePlayer(callbacks);
        Players.Add(player);
        CreatePlayerOverride?.Invoke(player);
        return player;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>One fake player: a scriptable Parse/Play outcome and manual triggers for every native
/// callback and event D-video.md § 4.5's table depends on, so the state machine can be driven row by
/// row without LibVLC.</summary>
public sealed class FakePlayer : IBackendPlayer
{
    private readonly IPlayerCallbacks _callbacks;

    public FakePlayer(IPlayerCallbacks callbacks) => _callbacks = callbacks;

    public int ParseCalls { get; private set; }
    public int PlayCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int RestartCalls { get; private set; }
    public int SetPauseCalls { get; private set; }
    public bool IsDisposed { get; private set; }
    public bool IsPaused { get; private set; }
    public string? LastParsedPath { get; private set; }

    /// <summary>The generation captured by the last successful <see cref="Play"/> — what every
    /// Raise* method below stamps onto its callback, exactly like a real backend would.</summary>
    public int LastGeneration { get; private set; }

    /// <summary>What the next <see cref="Parse"/> call returns.</summary>
    public ParseResult NextParseResult { get; set; } = ParseResult.HasVideo(1920, 1080, "av01");

    /// <summary>What the next <see cref="Play"/> call returns.</summary>
    public bool NextPlayResult { get; set; } = true;

    public BackendStatistics StatisticsToReport { get; set; }
    public TimeSpan LengthToReport { get; set; } = TimeSpan.FromSeconds(8);

    public ParseResult Parse(string path, TimeSpan timeout)
    {
        ParseCalls++;
        LastParsedPath = path;
        return NextParseResult;
    }

    public bool Play(VideoOptions options, int generation)
    {
        PlayCalls++;
        LastGeneration = generation;
        return NextPlayResult;
    }

    public void Restart() => RestartCalls++;

    public void Stop()
    {
        StopCalls++;
        IsPaused = false;
    }

    public void SetPause(bool paused)
    {
        SetPauseCalls++;
        IsPaused = paused;
    }

    public BackendStatistics Statistics() => StatisticsToReport;

    public TimeSpan Length => LengthToReport;

    public void Dispose() => IsDisposed = true;

    // ---- test-driven simulation of the four callbacks + two events, stamped with the last Play's
    //      generation exactly like a real backend would from its own native-thread callbacks. ----

    public (int Width, int Height, int Pitch, int Lines) RaiseFormat(int sourceWidth, int sourceHeight) =>
        _callbacks.OnFormat(sourceWidth, sourceHeight, LastGeneration);

    public void RaiseCleanup() => _callbacks.OnCleanup(LastGeneration);

    public IntPtr RaiseLock() => _callbacks.OnLock(LastGeneration);

    public void RaiseDisplay() => _callbacks.OnDisplay(LastGeneration);

    public void RaiseEncounteredError() => _callbacks.OnEncounteredError(LastGeneration);

    public void RaiseEndReached() => _callbacks.OnEndReached(LastGeneration);

    /// <summary>Convenience: format then display, i.e. "a frame arrived" — enough to exercise
    /// FrameStore's real allocate/copy path without a real decoder behind it.</summary>
    public void RaiseFrame(int sourceWidth = 1920, int sourceHeight = 1080)
    {
        RaiseFormat(sourceWidth, sourceHeight);
        RaiseLock();
        RaiseDisplay();
    }
}
