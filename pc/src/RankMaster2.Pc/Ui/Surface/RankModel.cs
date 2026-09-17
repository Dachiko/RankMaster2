namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;

/// <summary>
/// The compare screen as data (plan § 2.1). Everything <see cref="RankCoordinator"/> decides with is
/// read from here; everything <c>Views/RankView</c> draws is read from here. No Avalonia type, no
/// method that talks to B, C or D — that is <see cref="RankCoordinator"/>'s job.
/// </summary>
public sealed class RankModel
{
    public Snapshot? Snapshot { get; private set; }
    public PaneState? Left { get; private set; }
    public PaneState? Right { get; private set; }

    public bool Busy { get; private set; }
    public DateTimeOffset? InFlightSince { get; private set; }

    /// <summary>Set while the ~100 ms select cue is playing, and which pane is flashing. Null when
    /// no cue is in progress (skip/discard/special/undo/save never set it).</summary>
    public Side? CueSide { get; private set; }

    /// <summary><c>Esc</c> arrived while a cue was playing or a call was in flight. Checked when the
    /// cue completes (don't vote) and set immediately raises <c>QuitRequested</c> either way
    /// (plan § 3.1, "Esc during the cue").</summary>
    public bool Quitting { get; private set; }

    public DateTimeOffset PairArrivedAt { get; private set; }

    public (string Text, DateTimeOffset ExpiresAt)? Toast { get; private set; }

    public bool HelpPinned { get; set; }
    public bool DialogOpen { get; set; }

    public IReadOnlyList<string> Strip => Snapshot?.Cues ?? Array.Empty<string>();

    public long CurrentPairSeq => Snapshot?.PairSeq ?? -1;

    private readonly HashSet<UiKey> _held = new();
    private readonly HashSet<UiKey> _consumedWhileHeld = new();

    // ---- key bookkeeping (gate rule 3) -------------------------------------------------------------

    public void KeyDown(UiKey key) => _held.Add(key);

    public void KeyUp(UiKey key)
    {
        _held.Remove(key);
        _consumedWhileHeld.Remove(key);
    }

    public bool IsHeld(UiKey key) => _held.Contains(key);

    /// <summary>True if this key (or "no key", for a mouse click) may cause a new accepted action.</summary>
    public bool KeyReleasedSinceLastAccept(UiKey? key) => key is null || !_consumedWhileHeld.Contains(key.Value);

    public void MarkConsumed(UiKey? key)
    {
        if (key is { } k) _consumedWhileHeld.Add(k);
    }

    // ---- gate plumbing ------------------------------------------------------------------------------

    public bool PanesReadyFor(Intent intent) =>
        Left is not null && Right is not null && Left.Accepts(intent) && Right.Accepts(intent);

    public GateInputs BuildGateInputs(Intent intent, UiKey? key, IClock clock)
    {
        var now = clock.UtcNow;
        var pairSeq = CurrentPairSeq;
        return new GateInputs(
            Busy: Busy,
            PanesReady: intent.IsBusyOnlyAction() || PanesReadyFor(intent),
            KeyReleasedSinceLastAccept: KeyReleasedSinceLastAccept(key),
            Now: now,
            PairArrivedAt: PairArrivedAt,
            RequestedPairSeq: pairSeq,
            CurrentPairSeq: pairSeq);
    }

    // ---- mutation, all called by RankCoordinator only -----------------------------------------------

    public void EnterBusy(DateTimeOffset now)
    {
        Busy = true;
        InFlightSince = now;
    }

    public void ExitBusy()
    {
        Busy = false;
        InFlightSince = null;
    }

    public void BeginCue(Side side) => CueSide = side;
    public void EndCue() => CueSide = null;

    public void RequestQuit() => Quitting = true;

    /// <summary>Consumes the quitting flag, returning what it was. Called once when a cue or an
    /// in-flight call completes, so a second completion does not re-read a stale "don't send" flag.</summary>
    public bool TakeQuitting()
    {
        var was = Quitting;
        return was;
    }

    public void SetSnapshot(Snapshot snapshot) => Snapshot = snapshot;

    public void SetPanes(PaneState? left, PaneState? right, DateTimeOffset arrivedAt)
    {
        Left = left;
        Right = right;
        PairArrivedAt = arrivedAt;
    }

    public void SetToast(string text, IClock clock) => Toast = (text, clock.UtcNow + Timings.ToastMs);

    public void ClearToastIfExpired(IClock clock)
    {
        if (Toast is { } t && clock.UtcNow >= t.ExpiresAt) Toast = null;
    }

    public void ClearToast() => Toast = null;
}
