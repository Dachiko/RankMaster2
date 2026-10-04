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

    /// <summary>The folder's own name, for the ink card's Doto line and the result line (plan H § 3.2).</summary>
    public string FolderName => Notices.LeafName(Folder);

    /// <summary>Plan H § 3.2's one line under the folder name. It replaces the old paragraph
    /// (§ 3.13 item 2's wording: names become 000001-xxxx.jpg, nothing is copied, no rating is lost,
    /// Cancel stops where it is): the card says only what is new to him -- the name pattern -- and
    /// that the ratings stay. The cancel sentence now appears only if he cancels (the result line).</summary>
    public const string ConfirmLine = "000001-….jpg · ratings stay";

    /// <summary>0..100, rounded down so the card never says 100 before the run is done. Plan H § 3.2:
    /// the card shows a percent, <b>never</b> "done / total" (house rule, DESIGN.md: progress is
    /// 0–100 %). 0 while the total is not known yet (the "preparing" phase).</summary>
    public int Percent => Total > 0 ? Math.Clamp((int)(100L * Done / Total), 0, 100) : 0;

    /// <summary>Which of the footer's three phase words is current (plan H § 3.2:
    /// <c>PREPARE · RENAME · SAVE</c>): 0, 1 or 2. <c>reuniting</c> and <c>done</c> read as SAVE
    /// (the ratings being written back is the save); an unknown phase reads as the first.</summary>
    public int PhaseStep => Phase switch
    {
        "renaming" => 1,
        "saving" or "reuniting" or "done" => 2,
        _ => 0,
    };

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
