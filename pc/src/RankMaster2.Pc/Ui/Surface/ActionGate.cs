namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// Plan § 3.1: the five rules that make one press one action. Stateless and pure — every input the
/// rules need is a parameter, so the test suite can hit every combination without constructing a
/// model. <see cref="RankModel"/> owns the state (busy, held keys, arrival time); this just answers
/// the question.
///
/// Only the seven pair actions (vote/skip/discard/special) and the two busy-only actions
/// (undo/save) are ever passed here. OpenFolder, ToggleHelp and Quit have their own rules
/// (plan § 1.1: "not while a dialog is open" / "none" / "none") and never reach this gate.
/// </summary>
public static class ActionGate
{
    public static GateVerdict Try(Intent intent, GateInputs input)
    {
        // Rule 1: busy. An action is in flight, cue included, until its result is applied.
        if (input.Busy)
            return GateVerdict.Drop(DropReason.Busy);

        // Rule 3: released. The physical key that carries this intent must have been released since
        // it last caused an accepted action. Mouse clicks are always "released" (a click is
        // pressed-and-released in one event) and pass this trivially -- callers report that as
        // KeyReleasedSinceLastAccept: true.
        if (!input.KeyReleasedSinceLastAccept)
            return GateVerdict.Drop(DropReason.KeyStillHeld);

        if (!intent.IsBusyOnlyAction())
        {
            // Rule 2: ready. Skipped entirely for Undo/Save (plan § 3.1: "Undo and save skip this rule").
            if (!input.PanesReady)
                return GateVerdict.Drop(DropReason.NotReady);

            // Rule 4: arrival guard. Also skipped for Undo/Save -- neither targets "the pair on
            // screen" the way a vote or a file action does.
            if (input.Now - input.PairArrivedAt < Timings.ArrivalGuardMs)
                return GateVerdict.Drop(DropReason.ArrivalGuard);
        }

        // Rule 5: target. The intent must still name the pair the model displayed when the key was
        // accepted. Under rule 1 this cannot actually differ (nothing else can run while busy), but
        // the check is one line and it is what makes rule 5 an invariant rather than a hope.
        if (input.RequestedPairSeq != input.CurrentPairSeq)
            return GateVerdict.Drop(DropReason.TargetMismatch);

        return GateVerdict.Accept;
    }
}

/// <summary>Everything <see cref="ActionGate.Try"/> needs to decide, gathered by the caller from
/// <see cref="RankModel"/> at the moment a key or click arrives.</summary>
public readonly record struct GateInputs(
    bool Busy,
    bool PanesReady,
    bool KeyReleasedSinceLastAccept,
    DateTimeOffset Now,
    DateTimeOffset PairArrivedAt,
    long RequestedPairSeq,
    long CurrentPairSeq);

public enum DropReason
{
    Busy,
    NotReady,
    KeyStillHeld,
    ArrivalGuard,
    TargetMismatch,
}

public abstract record GateVerdict
{
    private GateVerdict() { }

    public sealed record Accepted : GateVerdict;
    public sealed record Dropped(DropReason Reason) : GateVerdict;

    public static readonly GateVerdict Accept = new Accepted();
    public static GateVerdict Drop(DropReason reason) => new Dropped(reason);

    public bool IsAccepted => this is Accepted;
}
