namespace RankMaster2.Pc.App;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Stills;

/// <summary>
/// A-startup-and-shell.md § 3.2. Part A's decorator around part B's real <see cref="ISessionLink"/>
/// — part E receives this and is none the wiser. The whole of part A's control over "when the video
/// engine wakes": the first <see cref="OpenAsync"/> of a folder starts the server round trip and
/// C's folder probe concurrently, warming the engine only if either says the folder is video. Every
/// other member is a one-line delegate.
/// </summary>
public sealed class WakingSessionLink : ISessionLink
{
    private readonly ISessionLink _inner;
    private readonly IMediaProbe _probe;
    private readonly VideoEngineGate _gate;

    public WakingSessionLink(ISessionLink inner, IMediaProbe probe, VideoEngineGate gate)
    {
        _inner = inner;
        _probe = probe;
        _gate = gate;
    }

    public LinkState State => _inner.State;
    public Snapshot? Snapshot => _inner.Snapshot;
    public Failure? LastFailure => _inner.LastFailure;
    public bool IsBusy => _inner.IsBusy;

    public Task<ConnectResult> ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);

    public async Task<OpenResult> OpenAsync(string folder, CancellationToken ct = default)
    {
        StartupClock.Mark("open_requested");

        var open = _inner.OpenAsync(folder, ct); // started first, not awaited yet (§ 3.2)

        _ = Task.Run(async () =>
        {
            try
            {
                var media = await _probe.ProbeAsync(folder, ct).ConfigureAwait(false);
                var needs = media.NeedsVideoEngine;
                StartupClock.Mark(needs ? "probe_video" : "probe_still");
                if (needs)
                    await _gate.WarmUpAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                StartupClock.Mark("probe_failed"); // an unreadable folder decides nothing (§ 3.2)
            }
            catch (UnauthorizedAccessException)
            {
                StartupClock.Mark("probe_failed");
            }
        }, ct);

        var result = await open.ConfigureAwait(false);

        if (result is OpenResult.Opened opened)
        {
            StartupClock.Mark("snapshot");
            if (string.Equals(opened.Snapshot.Policy, "video", StringComparison.OrdinalIgnoreCase))
                _ = _gate.WarmUpAsync(ct); // authoritative and idempotent; catches a probe false negative, late but correct
        }

        return result;
    }

    public Task<ActionResult> VoteAsync(Side winner, long onPairSeq, CancellationToken ct = default) =>
        _inner.VoteAsync(winner, onPairSeq, ct);

    public Task<ActionResult> SkipAsync(long onPairSeq, CancellationToken ct = default) =>
        _inner.SkipAsync(onPairSeq, ct);

    public Task<ActionResult> DiscardAsync(Side side, long onPairSeq, CancellationToken ct = default) =>
        _inner.DiscardAsync(side, onPairSeq, ct);

    public Task<ActionResult> SpecialAsync(Side side, long onPairSeq, CancellationToken ct = default) =>
        _inner.SpecialAsync(side, onPairSeq, ct);

    public Task<ActionResult> UndoAsync(CancellationToken ct = default) => _inner.UndoAsync(ct);

    public Task<ActionResult> SaveAsync(CancellationToken ct = default) => _inner.SaveAsync(ct);

    public Task<ActionResult> RefreshAsync(CancellationToken ct = default) => _inner.RefreshAsync(ct);

    public Task CloseAsync(CancellationToken ct = default) => _inner.CloseAsync(ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
