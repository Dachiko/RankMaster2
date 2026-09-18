namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Video;

public enum AppScreen { Start, Rank, Rename }

/// <summary>
/// Intent → (release handles) → <see cref="ISessionLink"/> call → apply result → prefetch (plan
/// § 2.1). The only class that talks to B, C and D. Owns both <see cref="RankModel"/> and
/// <see cref="StartModel"/> because they share one link and one session lifecycle — Resume and
/// Open on the start screen go through exactly the same <see cref="ISessionLink"/> the compare
/// screen's actions do.
/// <para/>
/// <c>Views/</c> calls every public entry point here on the UI thread, so whatever runs before the
/// first await is already on it. After that the rule has to be kept deliberately:
/// Every await of B, C or D uses <c>ConfigureAwait(false)</c>, so the continuation runs wherever
/// that call finished -- a thread-pool thread. Nothing after such an await touches
/// <see cref="Rank"/>, <see cref="Start"/> or <see cref="Rename"/>, or raises
/// <see cref="Changed"/>, except inside <c>_ui.InvokeAsync(...)</c>: <see cref="IUiThread"/>'s
/// contract is that <b>everything</b> here that reads or writes a model does so on the UI thread,
/// and AUDIT2.md § 4.5 is the report of this class not keeping it.
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
    private bool _lastActionLate;

    /// <summary>Increments every time a folder is (re)entered from the start screen (Open, Resume,
    /// or Ctrl+Z bringing the exhausted screen back to ranking). Views uses it to know when to reset
    /// per-open state, such as the <c>panes_painted</c> startup mark (plan § A22), without needing a
    /// fresh <c>RankView</c> instance for every open.</summary>
    public int OpenSequence { get; private set; }

    public RankModel Rank { get; } = new();
    public StartModel Start { get; } = new();
    public RenameModel Rename { get; } = new();
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
        await _ui.InvokeAsync(RaiseChanged).ConfigureAwait(false);
    }

    /// <summary>Plan § 2.4 (A20): a 250 ms tick from Views. Clears an expired toast and notices when
    /// the late-action line's visibility just crossed its threshold, raising <see cref="Changed"/>
    /// only when one of those actually changed -- most ticks repaint nothing.</summary>
    public void Tick()
    {
        var hadToast = Rank.Toast is not null;
        Rank.ClearToastIfExpired(_clock);
        var toastChanged = hadToast && Rank.Toast is null;

        var wasLate = _lastActionLate;
        _lastActionLate = Rank.Busy && Rank.InFlightSince is { } since
            && _clock.UtcNow - since >= Timings.LateActionLineAfterMs;

        if (toastChanged || _lastActionLate != wasLate) RaiseChanged();

        // § 3.13 item 1: the rename progress bar is driven by the same 250 ms tick that already
        // clears toasts and the late-action line -- not a second timer, and never a poll overlapping
        // one already in flight (Busy guards that; a pending cancel rides the next poll's own
        // finally, PollRenameOnceAsync).
        if (Screen == AppScreen.Rename && Rename.Stage == RenameStage.Running && !Rename.Busy)
            _ = PollRenameOnceAsync();
    }

    /// <summary>H10: the physical size of one pane, in device pixels -- forwarded to the still
    /// source so stills decode at the size they are actually shown at instead of the constructor
    /// default. Views calls this from layout (Loaded/SizeChanged); the matching call for video,
    /// <c>IVideoSurface.SetPaneSize</c>, is Avalonia-typed and made by Views directly against
    /// <see cref="LeftVideoSurface"/>/<see cref="RightVideoSurface"/> (see the class doc).</summary>
    public void SetPaneSize(int widthPx, int heightPx) => _stills.SetPaneSize(widthPx, heightPx);

    // ---- start screen ----------------------------------------------------------------------------

    // Which screen's flag BeginDialog set, so EndDialog clears that same one even if the screen
    // changed while the dialog was open -- exactly what RenameViaPicker does: it begins on Start,
    // then BeginRenameConfirm switches to Rename before the picker's `finally` runs EndDialog. Read
    // Screen fresh at both ends used to clear Rank.DialogOpen there, leaving Start.DialogOpen stuck
    // true forever and every later "Open folder" click a silent no-op.
    private AppScreen? _dialogScreen;

    public void BeginDialog()
    {
        _dialogScreen = Screen;
        if (Screen == AppScreen.Start) Start.DialogOpen = true; else Rank.DialogOpen = true;
        RaiseChanged();
    }

    public void EndDialog()
    {
        var screen = _dialogScreen ?? Screen;
        _dialogScreen = null;
        if (screen == AppScreen.Start) Start.DialogOpen = false; else Rank.DialogOpen = false;
        RaiseChanged();
    }

    // ---- rename by rank (§ 3.13, SERVER_SPEC.md § 10.16) ------------------------------------------

    /// <summary>The folder was just picked (the same native dialog Open uses). Moves to the rename
    /// screen's confirmation stage — nothing is sent to the server yet.</summary>
    public void BeginRenameConfirm(string folder)
    {
        Rename.BeginConfirm(folder);
        Screen = AppScreen.Rename;
        RaiseChanged();
    }

    /// <summary>"No" on the confirmation, or Esc while it is showing (§ 3.13: "Esc on this screen
    /// cancels the rename, not the program" — before anything has started, that is simply not
    /// starting it).</summary>
    public void CancelRenameConfirm()
    {
        if (Screen != AppScreen.Rename || Rename.Stage != RenameStage.Confirming) return;
        Screen = AppScreen.Start;
        RaiseChanged();
    }

    /// <summary>"Yes": opens the folder if it is not already this client's open session (a rename is
    /// one of the <c>/session*</c> routes, § 10.16), then <c>POST /session/rename</c>. From here the
    /// 250 ms tick (<see cref="Tick"/>) polls <c>GET /session/rename</c> until a terminal state.</summary>
    public async Task ConfirmRenameAsync(CancellationToken ct = default)
    {
        if (Screen != AppScreen.Rename || Rename.Stage != RenameStage.Confirming) return;
        var folder = Rename.Folder;

        Rename.EnterBusy();
        RaiseChanged();

        try
        {
            if (_link.Snapshot is not { } snapshot || !FolderEquals(snapshot.Folder, folder))
            {
                OpenResult opened;
                try { opened = await _link.OpenAsync(folder, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    await _ui.InvokeAsync(() => ReturnToStartWithMessage($"Could not open the folder: {ex.Message}")).ConfigureAwait(false);
                    return;
                }

                if (opened is OpenResult.Failed failed)
                {
                    await _ui.InvokeAsync(() => ReturnToStartWithMessage(Notices.ForOpenFailure(failed.Failure).Text ?? failed.Failure.Title)).ConfigureAwait(false);
                    return;
                }
            }

            RenameOperationResult started;
            try { started = await _link.StartRenameAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                await _ui.InvokeAsync(() => ReturnToStartWithMessage($"Could not start the rename: {ex.Message}")).ConfigureAwait(false);
                return;
            }

            await _ui.InvokeAsync(() =>
            {
                switch (started)
                {
                    case RenameOperationResult.Observed(var op):
                        Rename.BeginRunning(op);
                        if (op.IsTerminal) FinishRename(op);
                        break;

                    default:
                        ReturnToStartWithMessage(DescribeRenameRefusal(started));
                        break;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                Rename.ExitBusy();
                RaiseChanged();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Esc, or the Cancel button, while the run is in progress. Idempotent on this side
    /// too: a second press while the first cancel is already in flight, or after the run is already
    /// terminal, does nothing further (§ 10.16: cancel itself is idempotent). If a poll is already
    /// in flight the cancel is sent the moment it returns (<see cref="PollRenameOnceAsync"/>) — never
    /// queued behind a whole extra 250 ms tick, and never overlapped with it (the link is one call
    /// at a time).</summary>
    public void RequestCancelRename()
    {
        if (Screen != AppScreen.Rename || Rename.Stage != RenameStage.Running) return;
        var alreadyRequested = Rename.CancelRequested;
        Rename.RequestCancel();
        if (!alreadyRequested && !Rename.Busy) _ = FireCancelRenameAsync();
        RaiseChanged();
    }

    /// <summary>Esc on the rename screen, whichever stage it is in (§ 3.13: it cancels the rename,
    /// not the program).</summary>
    public void OnRenameEscape()
    {
        if (Rename.Stage == RenameStage.Confirming) CancelRenameConfirm();
        else RequestCancelRename();
    }

    private async Task FireCancelRenameAsync()
    {
        Rename.EnterBusy();
        RaiseChanged();
        try
        {
            var result = await _link.CancelRenameAsync().ConfigureAwait(false);
            await _ui.InvokeAsync(() => ApplyRenamePollResult(result)).ConfigureAwait(false);
        }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                Rename.ExitBusy();
                RaiseChanged();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>One <c>GET /session/rename</c>, called from <see cref="Tick"/> every 250 ms while
    /// the run is in progress (§ 3.13 item 1: "the link polls ... every 250 ms" — done here, on the
    /// same cadence the repaint tick already uses, rather than inside <c>ISessionLink</c> itself,
    /// so Cancel stays a plain, un-overlapped call on that same one-at-a-time link).</summary>
    private async Task PollRenameOnceAsync()
    {
        Rename.EnterBusy();
        try
        {
            var result = await _link.GetRenameAsync().ConfigureAwait(false);
            await _ui.InvokeAsync(() => ApplyRenamePollResult(result)).ConfigureAwait(false);
        }
        finally
        {
            await _ui.InvokeAsync(Rename.ExitBusy).ConfigureAwait(false);
        }

        var cancelling = await _ui.InvokeAsync(() => Rename.CancelRequested && Rename.Stage == RenameStage.Running).ConfigureAwait(false);
        if (cancelling)
            await FireCancelRenameAsync().ConfigureAwait(false);

        await _ui.InvokeAsync(RaiseChanged).ConfigureAwait(false);
    }

    private void ApplyRenamePollResult(RenameOperationResult result)
    {
        switch (result)
        {
            case RenameOperationResult.Observed(var op):
                Rename.Apply(op);
                if (op.IsTerminal) FinishRename(op);
                break;

            case RenameOperationResult.NoOperation:
                // Nothing recorded yet (the POST landed after our journal fsync but before this GET,
                // or the plan's own race) -- the next tick tries again. Not an error.
                break;

            case RenameOperationResult.Refused(var failure) when IsRenameScreenEnding(failure):
                ReturnToStartWithMessage(string.IsNullOrEmpty(failure.Detail) ? failure.Title : $"{failure.Title} — {failure.Detail}");
                break;

            case RenameOperationResult.Refused:
                // Transient (busy, a lost response, ...): leave the last-known progress on screen
                // and let the next tick try again, the same "say nothing on a recoverable hiccup"
                // rule the compare screen already follows (plan § 3.5).
                break;
        }
    }

    /// <summary>A refusal while watching a rename is worth ending the screen over only when there is
    /// nothing left to watch: the session is gone, or the failure is one <see cref="Failure.Fatal"/>
    /// already marks as needing the start screen (a dead pairing, the wrong server). Everything else
    /// -- busy, a lost response, a timeout -- is exactly the kind of hiccup a slow USB drive causes
    /// and is not a reason to abandon the progress bar (§ 10.16's whole point).</summary>
    private static bool IsRenameScreenEnding(Failure failure) =>
        failure.Fatal || failure.Code == Codes.NoSession;

    private static string DescribeRenameRefusal(RenameOperationResult result) => result switch
    {
        RenameOperationResult.Refused(var failure) =>
            string.IsNullOrEmpty(failure.Detail) ? failure.Title : $"{failure.Title} — {failure.Detail}",
        RenameOperationResult.NoOperation => "The rename could not be started.",
        _ => "The rename could not be started.",
    };

    /// <summary>A terminal operation, observed either by a poll or by the cancel call's own answer:
    /// § 3.13 item 2 -- "one line says which and the start screen returns". The session the rename
    /// used is closed (fire-and-forget, matching Esc's own DELETE /session, § 2.2) so the folder's
    /// lock is free the moment the owner might want it back in Rank Master 2 (§ 6's checklist).</summary>
    private void FinishRename(RenameOperation operation)
    {
        var message = Notices.ForRenameTerminal(operation, Rename.Folder);
        _ = _link.CloseAsync();
        ReturnToStartWithMessage(message, isError: operation.State != "succeeded");
    }

    private void ReturnToStartWithMessage(string message, bool isError = true)
    {
        Screen = AppScreen.Start;
        Start.ShowMessage(message, isError);
        RaiseChanged();
    }

    private static bool FolderEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task<bool> OpenFolderAsync(string folder, CancellationToken ct = default)
    {
        Start.BeginOpening(folder);
        RaiseChanged();

        OpenResult result;
        try
        {
            result = await _link.OpenAsync(folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // H9: the link's busy gate throws (InvalidOperationException) when a call overlaps
            // another -- e.g. the startup ConnectAsync is still running when the owner's first press
            // is Open/Resume. Uncaught, this left Opening=true forever with both buttons disabled and
            // nothing said. Caught here, the start screen gets its buttons back and a reason.
            await _ui.InvokeAsync(() =>
            {
                Start.EndOpening();
                Start.ShowMessage($"Could not open the folder: {ex.Message}");
                RaiseChanged();
            }).ConfigureAwait(false);
            return false;
        }

        return await _ui.InvokeAsync(() =>
        {
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
        }).ConfigureAwait(false);
    }

    /// <summary>Plan § 4.1: "Ctrl+Z here calls the link's undo; if the snapshot that comes back is
    /// `ranking` the compare screen reopens on it; if it is still exhausted the sentence stays."</summary>
    public async Task TryUndoFromStartAsync(CancellationToken ct = default)
    {
        if (!Start.ExhaustedSessionOpen) return;
        if (_link.Snapshot?.UndoAvailable != true) return; // quiet no-op, same rule as the compare screen

        ActionResult result;
        try
        {
            result = await _link.UndoAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // H9, same gap as OpenFolderAsync: a thrown busy-gate exception must not wedge the
            // start screen's buttons.
            await _ui.InvokeAsync(() =>
            {
                Start.EndOpening();
                Start.ShowMessage($"Could not open the folder: {ex.Message}");
                RaiseChanged();
            }).ConfigureAwait(false);
            return;
        }

        await _ui.InvokeAsync(() =>
        {
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
        }).ConfigureAwait(false);
    }

    private void EnterCompareScreen(Snapshot snapshot)
    {
        ReleaseVideoSurfaces();
        Screen = AppScreen.Rank;
        Start.ClearBox();
        Rank.ClearToast();
        _consecutiveUnreachable = 0;
        OpenSequence++;
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
            case Intent.Quit:
                // A27: whether or not Avalonia routes Esc out of the native folder picker, a dialog
                // being open means Esc is the picker's to handle, not ours.
                if (Start.DialogOpen) return;
                Quit();
                return;
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
                // A27: same rule as the start screen -- a dialog open on this screen means Esc is
                // the picker's, not ours.
                if (Rank.DialogOpen) return Task.CompletedTask;
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
        // A21: SPEC says Esc quits immediately. ReleaseVideoSurfaces() calls IVideoSurface.Dispose,
        // which blocks waiting for the file to release (up to 4 s across both surfaces) -- fine
        // before a move, wrong here, since nobody is about to move a file. Stop() is asked for and
        // not waited on; the process is about to exit anyway, so nothing needs the surfaces disposed.
        Rank.RequestQuit();
        _ = LeftVideoSurface?.StopAsync();
        _ = RightVideoSurface?.StopAsync();
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

                var pairSeq = await _ui.InvokeAsync(() =>
                {
                    Rank.EndCue();
                    // Esc during the cue: quit already raised, nothing sent (plan § 3.1).
                    return Rank.IsQuitting ? (long?)null : Rank.CurrentPairSeq;
                }).ConfigureAwait(false);
                if (pairSeq is null)
                    return;

                var result = await _link.VoteAsync(side, pairSeq.Value).ConfigureAwait(false);
                await _ui.InvokeAsync(() => ApplyResult(RequestedAction.Vote, result)).ConfigureAwait(false);
            }
            else if (intent.IsDiscard() || intent.IsSpecial())
            {
                var side = intent.Subject()!.Value;
                await ReleaseHandlesBeforeMove(side).ConfigureAwait(false);

                var pairSeq = await _ui.InvokeAsync(() => Rank.CurrentPairSeq).ConfigureAwait(false);
                var result = intent.IsDiscard()
                    ? await _link.DiscardAsync(side, pairSeq).ConfigureAwait(false)
                    : await _link.SpecialAsync(side, pairSeq).ConfigureAwait(false);

                var requested = intent.IsDiscard() ? RequestedAction.Discard : RequestedAction.Special;
                await _ui.InvokeAsync(() => ApplyResult(requested, result)).ConfigureAwait(false);
            }
            else if (intent == Intent.Undo)
            {
                var result = await _link.UndoAsync().ConfigureAwait(false);
                await _ui.InvokeAsync(() => ApplyResult(RequestedAction.Undo, result)).ConfigureAwait(false);
            }
            else if (intent == Intent.Save)
            {
                var result = await _link.SaveAsync().ConfigureAwait(false);
                await _ui.InvokeAsync(() => ApplyResult(RequestedAction.Save, result)).ConfigureAwait(false);
            }
        }
        finally
        {
            await _ui.InvokeAsync(() =>
            {
                Rank.ExitBusy();
                RaiseChanged();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Plan § 3.3: release before the server moves a file. Bounded so a stuck release never
    /// blocks the request forever — it is sent anyway, and the server retries the move itself.</summary>
    private async Task ReleaseHandlesBeforeMove(Side side)
    {
        // Read on the UI thread, like everything else that reads the model (§ 4.5): this runs after
        // the cue's await, so the caller is no longer necessarily on it.
        var (pane, folder) = await _ui.InvokeAsync(() =>
            (side == Side.Left ? Rank.Left : Rank.Right, Rank.Snapshot?.Folder)).ConfigureAwait(false);
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
            // The pane's lease is still alive here and stays alive: it is a reference to a pixel
            // buffer in memory, not to the file, so it cannot be what stops the server moving the
            // file. It is disposed when this pane is replaced, which is what the result of this very
            // action will do (AUDIT2.md § 1.1: PaneControl.Render used to dispose it here, leaving a
            // kept pane pointing at freed memory).
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

    /// <summary>Runs on the UI thread, always: every caller wraps it in <c>_ui.InvokeAsync</c>
    /// (§ 4.5). It touches <see cref="Rank"/>, <see cref="Start"/> and <see cref="Screen"/>, all of
    /// which <c>Views/RankView.Refresh</c> reads on that thread with no barrier of its own.</summary>
    private void ApplyResult(RequestedAction requested, ActionResult result)
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
    }

    private void TransitionToExhausted(Snapshot snapshot)
    {
        ReleaseVideoSurfaces();
        ReleasePaneLeases();
        Screen = AppScreen.Start;
        Rank.ClearToast();
        var name = string.IsNullOrEmpty(snapshot.FolderName) ? snapshot.Folder : snapshot.FolderName;
        Start.ShowExhausted(name, snapshot.UndoAvailable);
    }

    private void TransitionToStartWithMessage(string text)
    {
        ReleaseVideoSurfaces();
        ReleasePaneLeases();
        Screen = AppScreen.Start;
        Rank.ClearToast();
        Start.ShowMessage(text);
    }

    /// <summary>Leaving the compare screen ends both panes: their leases are this class's to dispose
    /// (see <see cref="PaneState.Lease"/>), and the panes go with them so no later repaint can find a
    /// <see cref="PaneState"/> whose lease has been disposed.</summary>
    private void ReleasePaneLeases()
    {
        Rank.Left?.Lease?.Dispose();
        Rank.Right?.Lease?.Dispose();
        Rank.SetPanes(null, null, _clock.UtcNow);
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

        // Kept: the same picture, still on screen. The lease travels with it, alive -- nothing has
        // disposed it, and the frame behind it cannot be freed while it is held (AUDIT2.md § 1.1).
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
            var consumed = false;

            if (left is { IsVideo: false, Id: var lid } && lid == id && Accepts(left, state))
            {
                left = ApplyStillState(left, state);
                changed = true;
                consumed = true;
            }
            if (right is { IsVideo: false, Id: var rid } && rid == id && Accepts(right, state))
            {
                right = ApplyStillState(right, state);
                changed = true;
                consumed = true;
            }

            // H8: IStillSource.Show raises Changed for both ids unconditionally, with a fresh lease
            // each time, even for a pane that is already showing exactly that frame, superseded by a
            // later generation, or simply not this id any more. Nobody else owns a lease that arrives
            // here unconsumed -- dropped instead of disposed, it and the DecodeBudget bytes behind it
            // are never freed (H8: ~124 votes before every decode starts failing).
            if (!consumed && state is StillState.Ready ready)
                ready.Lease.Dispose();

            if (!changed) return;
            Rank.SetPanes(left, right, Rank.PairArrivedAt);
            RaiseChanged();
        });
    }

    /// <summary>
    /// Whether a delivery for this pane's id says anything this pane does not already show. A pane
    /// still waiting takes whatever arrives. A pane already showing pixels takes only a DIFFERENT
    /// frame: C re-decodes an id whose cached frame no longer covers the pane it is being asked to
    /// fill (<c>StillSource.EnsureFreshLocked</c>), which is exactly what happens to a pane kept for
    /// the next pair on a monitor bigger than the 960 x 1080 the first pair was decoded at, and the
    /// pane must move to the new frame rather than keep painting the small one. The repeat delivery
    /// of the frame it already holds -- which <c>Show</c> raises for every id, every time -- is not a
    /// change, and its lease is disposed by the caller.
    /// </summary>
    private static bool Accepts(PaneState pane, StillState state) => pane.Kind switch
    {
        PaneKind.Waiting or PaneKind.Refining => true,
        PaneKind.Ready => state is StillState.Ready ready && !ReferenceEquals(ready.Lease.Frame, pane.Lease?.Frame),
        _ => false,
    };

    private static PaneState ApplyStillState(PaneState pane, StillState state)
    {
        // Whatever this pane was holding is going: it is this class's to dispose (PaneState.Lease),
        // and the frame behind it stays alive for as long as any other lease on it does.
        switch (state)
        {
            case StillState.Ready ready:
                pane.Lease?.Dispose();
                return pane.WithReady(ready.Lease);
            case StillState.Failed { Reason: StillFailure.Missing }:
                pane.Lease?.Dispose();
                return pane.WithGone();
            case StillState.Failed { Reason: StillFailure.TooLarge }:
                // § 2.1: a picture C could not fit inside the decode budget is not a broken file and
                // is not described as one.
                pane.Lease?.Dispose();
                return pane.WithTooLarge();
            case StillState.Failed failed:
                pane.Lease?.Dispose();
                return pane.WithUndecodable(failed.Detail);
            default:
                return pane;
        }
    }

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
