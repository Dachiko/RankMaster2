namespace RankMaster2.Pc.Ui.Surface;

using RankMaster2.Pc.Link;
using RankMaster2.Pc.Link.Wire;

/// <summary>Where a notice appears, if anywhere. "Silent" is the model updating with nothing
/// written anywhere — plan § 3.5's default for everything that is not the owner's to fix.</summary>
public enum NoticeSurface { Silent, Toast, StartScreen }

public sealed record Notice(NoticeSurface Surface, string? Text)
{
    public static readonly Notice Silent = new(NoticeSurface.Silent, null);
    public static Notice Toast(string text) => new(NoticeSurface.Toast, text);
    public static Notice ForStartScreen(string text) => new(NoticeSurface.StartScreen, text);
}

/// <summary>The six things <see cref="RankCoordinator"/> can ask <see cref="ISessionLink"/> for.
/// <see cref="Notices"/> needs this because <see cref="Snapshot.LastAction"/> does not exist for a
/// save (it never touches the pair), so "what did we just ask for" cannot always be read back out
/// of the result alone.</summary>
public enum RequestedAction { Vote, Skip, Discard, Special, Undo, Save }

/// <summary>
/// lastAction → toast sentence; refusal/unreachable → toast, start-screen sentence, or silence
/// (plan § 3.4, § 3.5), built from the <em>real</em> <c>ISessionLink</c> (pc/src/RankMaster2.Pc/Link).
/// <para/>
/// That seam turned out to already carry the wording plan § 3.5's table asked E to build:
/// <see cref="Failure.Title"/>/<see cref="Failure.Detail"/> are, by the link's own doc comment,
/// "in words the surface shows as they are" — B absorbed "the words of the folder, not the
/// protocol" (plan § 1) itself. So this class does not re-derive English from a status code; it
/// picks Toast vs StartScreen from <see cref="Failure.Fatal"/> and otherwise shows B's sentence
/// unedited. What plan § 3.5 still leaves to E: which of Applied/Resynchronised/NotSent gets a
/// toast at all (vote and skip stay silent; file actions, undo and save do not — plan § 1.2), and
/// the exact undo sentence, which the plan itself specifies (§ 3.4) and B does not.
/// </summary>
public static class Notices
{
    public static Notice ForResult(RequestedAction requested, ActionResult result) => result switch
    {
        ActionResult.Applied applied => ForApplied(requested, applied.Snapshot),

        // "Adopted"/"Resynchronised": three of five cases carry the truth and need nothing said
        // (link's own doc comment on ActionResult). The one exception is LandedEarlier: B is
        // telling us this exact request applied on an earlier, lost-response attempt, which is
        // exactly Applied in every way that matters to the owner.
        ActionResult.Resynchronised { Why: ResyncReason.LandedEarlier } r => ForApplied(requested, r.Snapshot),
        ActionResult.Resynchronised => Notice.Silent,

        ActionResult.Refused refused => ForFailure(refused.Failure),
        ActionResult.Unknown unknown => ForFailure(unknown.Failure),

        // Nothing was transmitted. Not an error; the surface repaints from Snapshot and says nothing.
        ActionResult.NotSent => Notice.Silent,

        _ => Notice.Silent,
    };

    private static Notice ForApplied(RequestedAction requested, Snapshot snapshot) => requested switch
    {
        RequestedAction.Save => Notice.Toast("Saved"),
        RequestedAction.Undo => ForUndo(snapshot.LastAction),
        RequestedAction.Discard or RequestedAction.Special => ForFileAction(snapshot.LastAction),

        // Vote, Skip: "Toasts no longer echo every action... no 'vote recorded', no 'skipped'" (plan § 1.2).
        _ => Notice.Silent,
    };

    private static Notice ForFileAction(LastAction? lastAction)
    {
        if (lastAction is null) return Notice.Silent;
        return lastAction.Type switch
        {
            ActionTypes.Discard => Notice.Toast($"Discarded {lastAction.Id}"),
            ActionTypes.Special => Notice.Toast($"Moved {lastAction.Id} to special 1"),
            ActionTypes.DropMissing => Notice.Toast($"Removed {lastAction.Id} from the ranking"),
            _ => Notice.Silent,
        };
    }

    /// <summary>Plan § 3.4's table, verbatim. Public because <see cref="RankCoordinator"/> also
    /// needs it for the start screen's "Ctrl+Z takes back the last one" exhausted-undo path.</summary>
    public static Notice ForUndo(LastAction? lastAction)
    {
        if (lastAction?.UndoneType is null) return Notice.Silent;

        var suffix = lastAction.RestoredId is { } restored && !string.IsNullOrEmpty(lastAction.Id) && restored != lastAction.Id
            ? $" as {restored}"
            : "";

        return lastAction.UndoneType switch
        {
            ActionTypes.Vote => Notice.Toast("Vote taken back"),
            ActionTypes.Skip => Notice.Toast("Skip taken back"),
            ActionTypes.Discard => Notice.Toast($"Discard taken back{suffix}"),
            ActionTypes.Special => Notice.Toast($"Moved back out of special 1{suffix}"),
            _ => Notice.Silent,
        };
    }

    private static Notice ForFailure(Failure failure)
    {
        var text = string.IsNullOrEmpty(failure.Detail) ? failure.Title : $"{failure.Title} — {failure.Detail}";
        return failure.Fatal ? Notice.ForStartScreen(text) : Notice.Toast(text);
    }

    /// <summary>An open-folder refusal. Always the start screen's red box (plan § 4.1) — that is
    /// where Open lives — regardless of <see cref="Failure.Fatal"/>, which is about whether a
    /// *ranking* session must be abandoned, not about where an open failure is shown.</summary>
    public static Notice ForOpenFailure(Failure failure) =>
        Notice.ForStartScreen(string.IsNullOrEmpty(failure.Detail) ? failure.Title : $"{failure.Title} — {failure.Detail}");
}
