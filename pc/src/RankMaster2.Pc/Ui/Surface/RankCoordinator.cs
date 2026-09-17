namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Video;

public enum AppScreen { Start, Rank }

/// <summary>
/// Intent → (release handles) → <see cref="ISessionLink"/> call → apply result → prefetch (plan
/// § 2.1). The only class that talks to B, C and D. Owns both <see cref="RankModel"/> and
/// <see cref="StartModel"/> because they share one link and one session lifecycle — Resume and
/// Open on the start screen go through exactly the same <see cref="ISessionLink"/> the compare
/// screen's actions do.
/// <para/>
/// <see cref="Views"/> never touches B, C or D directly except for two Avalonia-typed calls this
/// class cannot make without importing Avalonia into <c>Surface/</c> (<c>IVideoSurface.SetPaneSize</c>
/// and reading <c>IVideoSurface.Frame</c>): Views/ reads the same <see cref="IVideoSurface"/>
/// instances back out through <see cref="LeftVideoSurface"/>/<see cref="RightVideoSurface"/> to do
/// those two things and nothing else.
/// </summary>
public sealed class RankCoordinator
{
    private readonly ISessionLink _link;
    private readonly IStillSource _stills;
    private readonly IVideoSurfaceFactory? _videoFactory;
    private readonly IClock _clock;
    private readonly IUiThread _ui;
    private readonly IDelay _delay;
    private readonly LastFolderStore _folderStore;

    private readonly HashSet<UiKey> _startConsumed = new();
    private int _consecutiveUnreachable;

    public RankModel Rank { get; } = new();
    public StartModel Start { get; } = new();
    public AppScreen Screen { get; private set; } = AppScreen.Start;

    /// <summary>Raised after any change Views should repaint for.</summary>
    public event Action? Changed;

    /// <summary>Raised by <c>Esc</c>. Plan § 2.2: A fires <c>DELETE /session</c> with a short
    /// timeout and exits; this class does not touch process lifetime.</summary>
    public event Action? QuitRequested;

    /// <summary>Raised by <c>O</c> (or the start screen's Open button) when no dialog is already
    /// open. Views shows the native picker and calls <see cref="OpenFolderAsync"/> with the result,
    /// bracketed by <see cref="BeginDialog"/>/<see cref="EndDialog"/>.</summary>
    public event Action? OpenFolderRequested;

    public IVideoSurface? LeftVideoSurface { get; private set; }
    public IVideoSurface? RightVideoSurface { get; private set; }

    public RankCoordinator(
        ISessionLink link,
        IStillSource stills,
        IVideoSurfaceFactory? videoFactory,
        IClock clock,
        IUiThread ui,
        IDelay delay,
        LastFolderStore? folderStore = null)
    {
        _link = link;
        _stills = stills;
        _videoFactory = videoFactory;
        _clock = clock;
        _ui = ui;
        _delay = delay;
        _folderStore = folderStore ?? new LastFolderStore();

        _stills.Changed += OnStillChanged;
        if (_videoFactory is not null) _videoFactory.EngineStatusChanged += OnEngineStatusChanged;
    }

    /// <summary>The start screen's one status line (plan § 4.1), read from the link's own state.</summary>
    public string? LinkStatusLine => StartModel.StatusLineFor(_link.State, _link.LastFailure);

    /// <summary>D's engine state, for the "Starting video…" line and <c>NoVideoEngine</c> panes.
    /// <c>Failed</c> when no factory was supplied at all (plan § 2.3's gap: see the report).</summary>
    public VideoEngineStatus VideoEngineStatus => _videoFactory?.EngineStatus ?? VideoEngineStatus.Failed;

    // ---- startup -------------------------------------------------------------------------------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Start.LastFolder = _folderStore.Load();
        await _link.ConnectAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    // ---- start screen ----------------------------------------------------------------------------

    public void BeginDialog()
    {
        if (Screen == AppScreen.Start) Start.DialogOpen = true; else Rank.DialogOpen = true;
        RaiseChanged();
    }

    public void EndDialog()
    {
        if (Screen == AppScreen.Start) Start.DialogOpen = false; else Rank.DialogOpen = false;
        RaiseChanged();
    }

    public async Task<bool> OpenFolderAsync(string folder, CancellationToken ct = default)
    {
        Start.BeginOpening(folder);
        RaiseChanged();

        var result = await _link.OpenAsync(folder, ct).ConfigureAwait(false);
        switch (result)
        {
            case OpenResult.Opened opened:
                Start.EndOpening();
                _folderStore.Save(folder);
                Start.LastFolder = folder;
                EnterCompareScreen(opened.Snapshot);
                RaiseChanged();
                return true;

            case OpenResult.Failed failed:
                Start.ShowMessage(Notices.ForOpenFailure(failed.Failure).Text ?? failed.Failure.Title);
                RaiseChanged();
                return false;

            default:
                return false;
        }
    }

    /// <summary>Plan § 4.1: "Ctrl+Z here calls the link's undo; if the snapshot that comes back is
    /// `ranking` the compare screen reopens on it; if it is still exhausted the sentence stays."</summary>
    public async Task TryUndoFromStartAsync(CancellationToken ct = default)
    {
        if (!Start.ExhaustedSessionOpen) return;
        if (_link.Snapshot?.UndoAvailable != true) return; // quiet no-op, same rule as the compare screen

        var result = await _link.UndoAsync(ct).ConfigureAwait(false);
        var snapshot = SnapshotOf(result);
        var notice = Notices.ForResult(RequestedAction.Undo, result);

        if (snapshot is not null)
        {
            Rank.SetSnapshot(snapshot);
            if (snapshot.IsRanking)
            {
                EnterCompareScreen(snapshot);
                if (notice.Surface == NoticeSurface.Toast) Rank.SetToast(notice.Text!, _clock);
            }
            else if (snapshot.IsExhausted)
            {
                TransitionToExhausted(snapshot);
            }
        }

        RaiseChanged();
    }

    private void EnterCompareScreen(Snapshot snapshot)
    {
        ReleaseVideoSurfaces();
        Screen = AppScreen.Rank;
        Start.ClearBox();
        Rank.ClearToast();
        _consecutiveUnreachable = 0;
        ApplySnapshotSync(snapshot);
    }

    // ---- compare screen: keys and clicks ------------------------------------------------------

    public Task OnCompareKeyDown(UiKey key, UiModifiers modifiers)
    {
        Rank.KeyDown(key);
        return Dispatch(KeyMap.MapCompare(key, modifiers), key);
    }

    public void OnCompareKeyUp(UiKey key) => Rank.KeyUp(key);

    public Task OnPaneClicked(Side side) => Dispatch(Intents.Vote(side), key: null);

    public Task OnActionButtonClicked(Intent intent) => Dispatch(intent, key: null);

    public void OnStartKeyDown(UiKey key, UiModifiers modifiers)
    {
        var intent = KeyMap.MapStart(key, modifiers);
        switch (intent)
        {
            case Intent.None: return;
            case Intent.Quit: Quit(); return;
            case Intent.ToggleHelp: Rank.HelpPinned = !Rank.HelpPinned; RaiseChanged(); return;
            case Intent.OpenFolder:
                if (!Start.DialogOpen) OpenFolderRequested?.Invoke();
                return;
            case Intent.Undo:
                if (_startConsumed.Contains(key)) return;
                _startConsumed.Add(key);
                _ = TryUndoFromStartAsync();
                return;
        }
    }

    public void OnStartKeyUp(UiKey key) => _startConsumed.Remove(key);

    private Task Dispatch(Intent intent, UiKey? key)
    {
        switch (intent)
        {
            case Intent.None:
                return Task.CompletedTask;

            case Intent.Quit:
                Quit();
                return Task.CompletedTask;

            case Intent.ToggleHelp:
                Rank.HelpPinned = !Rank.HelpPinned;
                RaiseChanged();
                return Task.CompletedTask;

            case Intent.OpenFolder:
                if (!Rank.DialogOpen) OpenFolderRequested?.Invoke();
                return Task.CompletedTask;
        }

        var inputs = Rank.BuildGateInputs(intent, key, _clock);
        if (!ActionGate.Try(intent, inputs).IsAccepted)
            return Task.CompletedTask; // dropped, always silent

        Rank.MarkConsumed(key);
        return RunAction(intent);
    }

    private void Quit()
    {
        Rank.RequestQuit();
        ReleaseVideoSurfaces();
        RaiseChanged();
        QuitRequested?.Invoke();
    }

    // ---- running an accepted action --------------------------------------------------------------

    private async Task RunAction(Intent intent)
    {
        if (intent == Intent.Undo && _link.Snapshot?.UndoAvailable != true)
        {
            // Quiet no-op (plan § 3.4): nothing sent, no toast, busy never set.
            return;
        }

        Rank.EnterBusy(_clock.UtcNow);
        RaiseChanged();
        try
        {
            if (intent.IsVote())
            {
                var side = intent == Intent.VoteLeft ? Side.Left : Side.Right;
                Rank.BeginCue(side);
                RaiseChanged();
                await _delay.Wait(Timings.CueMs).ConfigureAwait(false);
                Rank.EndCue();

                if (Rank.TakeQuitting())
                    return; // Esc during the cue: quit already raised, nothing sent (plan § 3.1)

                var result = await _link.VoteAsync(side, Rank.CurrentPairSeq).ConfigureAwait(false);
                await ApplyResult(RequestedAction.Vote, result).ConfigureAwait(false);
            }
            else if (intent == Intent.Skip)
            {
                var result = await _link.SkipAsync(Rank.CurrentPairSeq).ConfigureAwait(false);
                await ApplyResult(RequestedAction.Skip, result).ConfigureAwait(false);
            }
            else if (intent.IsDiscard() || intent.IsSpecial())
            {
                var side = intent.Subject()!.Value;
                await ReleaseHandlesBeforeMove(side).ConfigureAwait(false);

                var result = intent.IsDiscard()
                    ? await _link.DiscardAsync(side, Rank.CurrentPairSeq).ConfigureAwait(false)
                    : await _link.SpecialAsync(side, Rank.CurrentPairSeq).ConfigureAwait(false);

                await ApplyResult(intent.IsDiscard() ? RequestedAction.Discard : RequestedAction.Special, result)
                    .ConfigureAwait(false);
            }
            else if (intent == Intent.Undo)
            {
                var result = await _link.UndoAsync().ConfigureAwait(false);
                await ApplyResult(RequestedAction.Undo, result).ConfigureAwait(false);
            }
            else if (intent == Intent.Save)
            {
                var result = await _link.SaveAsync().ConfigureAwait(false);
                await ApplyResult(RequestedAction.Save, result).ConfigureAwait(false);
            }
        }
        finally
        {
            Rank.ExitBusy();
            RaiseChanged();
        }
    }

    /// <summary>Plan § 3.3: release before the server moves a file. Bounded so a stuck release never
    /// blocks the request forever — it is sent anyway, and the server retries the move itself.</summary>
    private async Task ReleaseHandlesBeforeMove(Side side)
    {
        var pane = side == Side.Left ? Rank.Left : Rank.Right;
        if (pane is null) return;

        if (pane.IsVideo)
        {
            var surface = side == Side.Left ? LeftVideoSurface : RightVideoSurface;
            if (surface is null) return;
            using var cts = new CancellationTokenSource(Timings.ReleaseWaitMaxMs);
            try { await surface.StopAsync().WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* send anyway */ }
        }
        else
        {
            var folder = Rank.Snapshot?.Folder;
            pane.Lease?.Dispose();
            if (folder is null) return;
            using var cts = new CancellationTokenSource(Timings.ReleaseWaitMaxMs);
            try { await _stills.ReleaseAsync(folder, pane.Id, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* send anyway */ }
        }
    }

    // ---- applying a result -------------------------------------------------------------------------

    private static Snapshot? SnapshotOf(ActionResult result) => result switch
    {
        ActionResult.Applied a => a.Snapshot,
        ActionResult.Resynchronised r => r.Snapshot,
        ActionResult.Refused { Snapshot: { } s } => s,
        _ => null,
    };

    private async Task ApplyResult(RequestedAction requested, ActionResult result)
    {
        var snapshot = SnapshotOf(result);
        if (snapshot is not null) ApplySnapshotSync(snapshot);

        var notice = Notices.ForResult(requested, result);

        if (snapshot is not null && snapshot.IsExhausted)
        {
            TransitionToExhausted(snapshot);
            return;
        }

        var fatal = result is ActionResult.Refused { Failure.Fatal: true } or ActionResult.Unknown { Failure.Fatal: true };
        if (fatal)
        {
            TransitionToStartWithMessage(notice.Text!);
            return;
        }

        if (result is ActionResult.Unknown)
        {
            _consecutiveUnreachable++;
            if (_consecutiveUnreachable >= 2)
            {
                TransitionToStartWithMessage(notice.Text ?? "The server did not answer.");
                _consecutiveUnreachable = 0;
                return;
            }
        }
        else
        {
            _consecutiveUnreachable = 0;
        }

        if (result is ActionResult.Refused { Snapshot: null })
        {
            // The session is simply gone and cannot be repainted (e.g. a reopen-after-no_session
            // that itself failed). Nothing left to show on the compare screen.
            TransitionToStartWithMessage(notice.Text ?? "The server closed this session.");
            return;
        }

        if (notice.Surface == NoticeSurface.Toast)
            Rank.SetToast(notice.Text!, _clock);

        await Task.CompletedTask;
    }

    private void TransitionToExhausted(Snapshot snapshot)
    {
        ReleaseVideoSurfaces();
        Screen = AppScreen.Start;
        Rank.ClearToast();
        var name = string.IsNullOrEmpty(snapshot.FolderName) ? snapshot.Folder : snapshot.FolderName;
        Start.ShowExhausted(name, snapshot.UndoAvailable);
    }

    private void TransitionToStartWithMessage(string text)
    {
        ReleaseVideoSurfaces();
        Screen = AppScreen.Start;
        Rank.ClearToast();
        Start.ShowMessage(text);
    }

    // ---- pane reconciliation (plan § 3.2) --------------------------------------------------------

    private void ApplySnapshotSync(Snapshot snapshot)
    {
        Rank.SetSnapshot(snapshot);
        var now = _clock.UtcNow;

        if (snapshot.Pair is null)
        {
            Rank.Left?.Lease?.Dispose();
            Rank.Right?.Lease?.Dispose();
            Rank.SetPanes(null, null, now);
            return;
        }

        var pair = snapshot.Pair;
        var newLeft = BuildPane(Side.Left, Rank.Left, pair.Left, snapshot.PairSeq, now);
        var newRight = BuildPane(Side.Right, Rank.Right, pair.Right, snapshot.PairSeq, now);
        Rank.SetPanes(newLeft, newRight, now);

        if (pair.Left.IsStill && pair.Right.IsStill &&
            (newLeft.Kind == PaneKind.Waiting || newRight.Kind == PaneKind.Waiting))
        {
            _stills.Show(snapshot.Folder, pair.Left.Id, pair.Right.Id);
        }

        RequestVideoIfNeeded(Side.Left, newLeft, snapshot.Folder);
        RequestVideoIfNeeded(Side.Right, newRight, snapshot.Folder);

        var warmTuples = snapshot.WarmPairs
            .Where(p => p.Left.IsStill && p.Right.IsStill)
            .Select(p => (p.Left.Id, p.Right.Id))
            .ToList();
        if (warmTuples.Count > 0)
            _stills.Warm(snapshot.Folder, warmTuples, snapshot.PrefetchPairs);
    }

    private static PaneState BuildPane(Side side, PaneState? old, MediaRef mediaRef, long pairSeq, DateTimeOffset now)
    {
        if (mediaRef.IsMissing)
        {
            old?.Lease?.Dispose();
            return PaneState.Waiting(side, pairSeq, mediaRef.Id, mediaRef.MediaVersion, mediaRef.IsVideo, now).WithGone();
        }

        if (old is not null && old.ReusableFor(mediaRef.Id, mediaRef.MediaVersion))
            return old.WithGeneration(pairSeq);

        old?.Lease?.Dispose();
        return PaneState.Waiting(side, pairSeq, mediaRef.Id, mediaRef.MediaVersion, mediaRef.IsVideo, now);
    }

    // ---- stills callback (plan § 3.2, § 2.3: "E marshals") ------------------------------------------

    private void OnStillChanged(string id, StillState state)
    {
        _ui.Post(() =>
        {
            var left = Rank.Left;
            var right = Rank.Right;
            var changed = false;

            if (left is { IsVideo: false, Id: var lid } && lid == id && left.Kind is PaneKind.Waiting or PaneKind.Refining)
            {
                left = ApplyStillState(left, state);
                changed = true;
            }
            if (right is { IsVideo: false, Id: var rid } && rid == id && right.Kind is PaneKind.Waiting or PaneKind.Refining)
            {
                right = ApplyStillState(right, state);
                changed = true;
            }

            if (!changed) return;
            Rank.SetPanes(left, right, Rank.PairArrivedAt);
            RaiseChanged();
        });
    }

    private static PaneState ApplyStillState(PaneState pane, StillState state) => state switch
    {
        StillState.Ready ready => pane.WithReady(ready.Lease),
        StillState.Failed { Reason: StillFailure.Missing } => pane.WithGone(),
        StillState.Failed failed => pane.WithUndecodable(failed.Detail),
        _ => pane,
    };

    // ---- video (plan § 3.6) --------------------------------------------------------------------

    private void RequestVideoIfNeeded(Side side, PaneState pane, string folder)
    {
        if (!pane.IsVideo || pane.Kind != PaneKind.Waiting) return;

        if (_videoFactory is null)
        {
            SetPane(side, pane.WithNoVideoEngine("no video engine is available on this build"));
            return;
        }

        if (_videoFactory.EngineStatus == VideoEngineStatus.Failed)
        {
            SetPane(side, pane.WithNoVideoEngine(_videoFactory.EngineFailure ?? "the video engine failed to start"));
            return;
        }

        var surface = GetOrCreateVideoSurface(side);
        surface.Play(Path.Combine(folder, pane.Id));
    }

    private IVideoSurface GetOrCreateVideoSurface(Side side)
    {
        if (side == Side.Left)
        {
            if (LeftVideoSurface is null)
            {
                LeftVideoSurface = _videoFactory!.Create("left");
                LeftVideoSurface.StateChanged += _ => OnVideoStateChanged(Side.Left);
            }
            return LeftVideoSurface;
        }

        if (RightVideoSurface is null)
        {
            RightVideoSurface = _videoFactory!.Create("right");
            RightVideoSurface.StateChanged += _ => OnVideoStateChanged(Side.Right);
        }
        return RightVideoSurface;
    }

    private void OnVideoStateChanged(Side side)
    {
        _ui.Post(() =>
        {
            var pane = side == Side.Left ? Rank.Left : Rank.Right;
            var surface = side == Side.Left ? LeftVideoSurface : RightVideoSurface;
            if (pane is null || surface is null || !pane.IsVideo || Rank.Snapshot is null) return;

            // Guard against a stale event for a path this pane is no longer showing (the old
            // VlcFramePlayer bug, plan § 2.3) -- each IVideoSurface belongs to exactly one pane, but
            // a late event from a previous Play on the same surface must still be checked.
            var expectedPath = Path.Combine(Rank.Snapshot.Folder, pane.Id);
            if (!string.Equals(surface.Path, expectedPath, StringComparison.Ordinal)) return;

            PaneState? updated = surface.State switch
            {
                VideoSurfaceState.Playing or VideoSurfaceState.Holding when pane.Kind == PaneKind.Waiting => pane.WithVideoReady(),
                VideoSurfaceState.Failed => MapVideoFailure(pane, surface.Failure),
                _ => null,
            };
            if (updated is null) return;

            SetPane(side, updated);
        });
    }

    private static PaneState MapVideoFailure(PaneState pane, VideoFailure? failure)
    {
        if (failure is null) return pane.WithUndecodable("the video could not be played");
        return failure.Kind switch
        {
            VideoFailureKind.Missing => pane.WithGone(),
            VideoFailureKind.EngineUnavailable => pane.WithNoVideoEngine(failure.Message),
            _ => pane.WithUndecodable(failure.Message),
        };
    }

    private void OnEngineStatusChanged(VideoEngineStatus status)
    {
        _ui.Post(() =>
        {
            if (status != VideoEngineStatus.Failed) { RaiseChanged(); return; }

            var reason = _videoFactory?.EngineFailure ?? "the video engine failed to start";
            if (Rank.Left is { IsVideo: true, Kind: PaneKind.Waiting } l) SetPane(Side.Left, l.WithNoVideoEngine(reason));
            if (Rank.Right is { IsVideo: true, Kind: PaneKind.Waiting } r) SetPane(Side.Right, r.WithNoVideoEngine(reason));
        });
    }

    private void SetPane(Side side, PaneState updated)
    {
        if (side == Side.Left) Rank.SetPanes(updated, Rank.Right, Rank.PairArrivedAt);
        else Rank.SetPanes(Rank.Left, updated, Rank.PairArrivedAt);
        RaiseChanged();
    }

    private void ReleaseVideoSurfaces()
    {
        LeftVideoSurface?.Dispose();
        LeftVideoSurface = null;
        RightVideoSurface?.Dispose();
        RightVideoSurface = null;
    }

    private void RaiseChanged() => Changed?.Invoke();
}
