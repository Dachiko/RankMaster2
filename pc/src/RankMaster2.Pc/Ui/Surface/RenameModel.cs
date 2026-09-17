namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link.Wire;

public enum RenameStage { Confirming, Running }

/// <summary>
/// The rename screen as data (§ 3.13; plan E's reserved slot, § E6 — "<c>RenameView</c> and the
/// start-screen button"). No Avalonia type; <c>Views/RenameView</c> binds to this and decides
/// nothing, the same rule <see cref="StartModel"/> and <see cref="RankModel"/> already follow.
/// <see cref="RankCoordinator"/> owns every transition; this class only holds what the result was.
/// </summary>
public sealed class RenameModel
{
    public RenameStage Stage { get; private set; } = RenameStage.Confirming;

    /// <summary>The folder this run is (or would be) renaming. Set once, when the owner picks it
    /// from the folder dialog; unchanged for the life of one confirm-then-run cycle.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>The owner's own words (§ 3.13 item 2), with the folder filled in.</summary>
    public string ConfirmText =>
        $"Rename every file in {Folder} by rank? Names become “000001-xxxx.jpg”, " +
        "“000002-xxxx.jpg”… Nothing is copied and no rating is lost. Cancel stops " +
        "where it is and undoes nothing already done.";

    /// <summary>True while a StartRename/GetRename/CancelRename call is in flight — the coordinator's
    /// guard against overlapping calls on the one-call-at-a-time link (ISessionLink's own rule).</summary>
    public bool Busy { get; private set; }

    public string OperationId { get; private set; } = "";
    public string State { get; private set; } = "";
    public string Phase { get; private set; } = "";
    public int Done { get; private set; }
    public int Total { get; private set; }

    /// <summary>Set the moment Esc or the Cancel button is used, so the screen can say
    /// "Cancelling…" at once even before the server has answered — cancel lands within one
    /// retry interval (SERVER_SPEC.md § 10.16), not instantly, and the owner should see that his
    /// press registered.</summary>
    public bool CancelRequested { get; private set; }

    public void BeginConfirm(string folder)
    {
        Stage = RenameStage.Confirming;
        Folder = folder;
        Busy = false;
        CancelRequested = false;
        OperationId = "";
        State = "";
        Phase = "";
        Done = 0;
        Total = 0;
    }

    public void BeginRunning(RenameOperation operation)
    {
        Stage = RenameStage.Running;
        Apply(operation);
    }

    public void Apply(RenameOperation operation)
    {
        OperationId = operation.OperationId;
        State = operation.State;
        Phase = operation.Phase;
        Done = operation.Done;
        Total = operation.Total;
    }

    public void RequestCancel() => CancelRequested = true;

    public void EnterBusy() => Busy = true;
    public void ExitBusy() => Busy = false;
}
