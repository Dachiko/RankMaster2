namespace RankMaster2.Pc.Tests.App.Fakes;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Video;

/// <summary>A-startup-and-shell.md § 8: "Fakes for B/C/D/E live in App/Fakes/ inside the test
/// project and implement the frozen seams only."</summary>
public sealed class FakeSessionLink : ISessionLink
{
    public LinkState State { get; set; } = LinkState.Connected;
    public Snapshot? Snapshot { get; set; }
    public Failure? LastFailure { get; set; }
    public bool IsBusy { get; set; }

    public Func<string, CancellationToken, Task<OpenResult>>? OnOpen { get; set; }
    public Func<CancellationToken, Task<ConnectResult>>? OnConnect { get; set; }
    public Func<CancellationToken, Task>? OnClose { get; set; }

    public int CloseCalls { get; private set; }
    public CancellationToken? LastCloseToken { get; private set; }

    public Task<ConnectResult> ConnectAsync(CancellationToken ct = default) =>
        OnConnect?.Invoke(ct) ?? Task.FromResult<ConnectResult>(new ConnectResult.Connected("https://127.0.0.1", false, false));

    public Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default) =>
        OnOpen?.Invoke(folder, ct) ?? Task.FromResult<OpenResult>(new OpenResult.Failed(TestSnapshots.SomeFailure));

    public Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default) => NotUsed();
    public Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default) => NotUsed();
    public Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default) => NotUsed();
    public Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default) => NotUsed();
    public Task<ActionResult> UndoAsync(CancellationToken ct = default) => NotUsed();
    public Task<ActionResult> SaveAsync(CancellationToken ct = default) => NotUsed();
    public Task<ActionResult> RefreshAsync(CancellationToken ct = default) => NotUsed();

    public async Task CloseAsync(CancellationToken ct = default)
    {
        CloseCalls++;
        LastCloseToken = ct;
        if (OnClose is not null)
            await OnClose(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task<ActionResult> NotUsed() => throw new NotSupportedException("not exercised by these tests");
}

public sealed class FakeMediaProbe : RankMaster2.Pc.Stills.IMediaProbe
{
    private readonly Func<string, CancellationToken, Task<FolderMedia>> _impl;

    private FakeMediaProbe(Func<string, CancellationToken, Task<FolderMedia>> impl) => _impl = impl;

    public static FakeMediaProbe Returns(FolderMedia media, TimeSpan? delay = null) =>
        new(async (_, ct) =>
        {
            if (delay is { } d)
                await Task.Delay(d, ct).ConfigureAwait(false);
            return media;
        });

    public static FakeMediaProbe Throws(Exception ex) => new((_, _) => throw ex);

    public Task<FolderMedia> ProbeAsync(string folder, CancellationToken ct) => _impl(folder, ct);
}

public sealed class FakeVideoSurfaceFactory : IVideoSurfaceFactory
{
    private readonly Func<CancellationToken, Task<VideoEngineStatus>>? _onWarmUp;

    public FakeVideoSurfaceFactory(Func<CancellationToken, Task<VideoEngineStatus>>? onWarmUp = null) => _onWarmUp = onWarmUp;

    public int WarmUpCalls { get; private set; }
    public VideoEngineStatus EngineStatus { get; private set; } = VideoEngineStatus.Asleep;
    public string? EngineFailure => null;
    public int LiveSurfaces => 0;

    public event Action<VideoEngineStatus>? EngineStatusChanged;

    public async Task<VideoEngineStatus> WarmUpAsync(CancellationToken ct = default)
    {
        WarmUpCalls++;
        EngineStatus = VideoEngineStatus.Starting;
        EngineStatusChanged?.Invoke(EngineStatus);
        var status = _onWarmUp is null ? VideoEngineStatus.Ready : await _onWarmUp(ct).ConfigureAwait(false);
        EngineStatus = status;
        EngineStatusChanged?.Invoke(status);
        return status;
    }

    public IVideoSurface Create(string paneName) => throw new NotSupportedException("not exercised by these tests");

    public string DiagnosticsDump() => $"FakeVideoSurfaceFactory: EngineStatus={EngineStatus}, WarmUpCalls={WarmUpCalls}";
}

public sealed class FakeStillSource : IStillSource
{
    public void SetPaneSize(int widthPx, int heightPx) { }
    public void Show(string folder, string leftId, string rightId) { }
    public void Warm(string folder, IReadOnlyList<(string LeftId, string RightId)> pairs, int prefetchPairs = 2) { }
    public StillState StateOf(string id) => new StillState.Pending();
    public event Action<string, StillState>? Changed;
    public Task<bool> ReleaseAsync(string folder, string id, CancellationToken ct) => Task.FromResult(true);
    public Task ReleaseAllAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Builds a <see cref="CompositionResult"/> out of fakes only — no network, no native
/// load — for tests that need to hand one to <c>MainWindow</c> without going through the real
/// <see cref="Composition.Build"/>.</summary>
public static class FakeComposition
{
    public static RankMaster2.Pc.App.CompositionResult Build(
        RankMaster2.Pc.App.Seams.PlaceholderRoot? root = null,
        FakeSessionLink? link = null,
        FakeVideoSurfaceFactory? engine = null)
    {
        link ??= new FakeSessionLink();
        engine ??= new FakeVideoSurfaceFactory();
        root ??= new RankMaster2.Pc.App.Seams.PlaceholderRoot("2.0.0-test");
        var gate = new RankMaster2.Pc.App.VideoEngineGate(engine, nativeDir: null);
        var lifetime = new RankMaster2.Pc.App.AppLifetime(link);

        return new RankMaster2.Pc.App.CompositionResult(link, new FakeStillSource(), engine, gate, lifetime, root);
    }
}

/// <summary>Minimal, valid <see cref="Snapshot"/> values for tests that only care about
/// <c>Policy</c>.</summary>
public static class TestSnapshots
{
    public static Snapshot With(string policy) => new(
        SessionId: "s1",
        State: "ranking",
        Folder: "/tmp/folder",
        FolderName: "folder",
        Policy: policy,
        OpenedAt: "2026-01-01T00:00:00Z",
        PrefetchPairs: 0,
        SessionVotes: 0,
        Counts: new Counts(0, 0, 0, 0, 0),
        Progress: 0,
        ProgressPercent: 0,
        Cues: Array.Empty<string>(),
        Pair: null,
        PairToken: null,
        PairSeq: 0,
        WarmPairs: Array.Empty<Pair>(),
        UndoAvailable: false,
        LastAction: null,
        LastSavedAt: null);

    public static Failure SomeFailure => new(
        Kind: FailureKind.Unreachable,
        Title: "test",
        Detail: "test",
        Code: "client_test",
        RequestId: null,
        Fatal: false);
}
