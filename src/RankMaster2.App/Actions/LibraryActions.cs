namespace RankMaster2;

/// <summary>Discard / special 1 / undo / rename. Next phase.</summary>
public sealed class LibraryActions : ILibraryActions
{
    public void Discard(MediaId id) =>
        throw new NotImplementedException("Discard — see SPEC.md.");

    public void MoveToSpecial(MediaId id) =>
        throw new NotImplementedException("Move to special 1 — see SPEC.md.");

    public bool UndoLastMove() =>
        throw new NotImplementedException("Undo last move — see SPEC.md.");

    public void RenameByRank() =>
        throw new NotImplementedException("Rename by μ − 3σ — see SPEC.md.");
}
