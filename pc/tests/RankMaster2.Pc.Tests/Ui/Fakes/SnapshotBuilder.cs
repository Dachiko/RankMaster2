using RankMaster2.Pc.Link.Wire;
// This file's namespace nests under `RankMaster2`, which also declares its own `Pair` (Core's
// contracts type, MediaId x MediaId). Enclosing-namespace member lookup finds that one before ANY
// using directive is consulted -- even a `using Pair = ...` alias -- so every use of `Pair` below is
// fully qualified as RankMaster2.Pc.Link.Wire.Pair rather than left to resolve on its own.

namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>Builds a <see cref="Snapshot"/> with sane defaults so a test only names what it cares
/// about. Field-for-field with <c>SERVER_SPEC.md</c> § 9.1 / <c>Link/Wire/Snapshot.cs</c>.</summary>
public static class SnapshotBuilder
{
    public static MediaRef Media(string id, string kind = "still", long? sizeBytes = 12345, string? mediaVersion = "v1") =>
        new(id, kind, sizeBytes, mediaVersion, new Links("meta", kind == "still" ? "still" : null, null, kind == "video" ? "video" : null));

    public static Snapshot Ranking(
        string folder = "/lib",
        string? folderName = null,
        string leftId = "a.jpg",
        string rightId = "b.jpg",
        string kind = "still",
        long pairSeq = 1,
        int sessionVotes = 0,
        int unranked = 10,
        bool undoAvailable = false,
        LastAction? lastAction = null,
        IReadOnlyList<string>? cues = null,
        IReadOnlyList<RankMaster2.Pc.Link.Wire.Pair>? warmPairs = null,
        bool leftMissing = false,
        bool rightMissing = false,
        string? pairToken = "tok-1")
    {
        var left = Media(leftId, kind, leftMissing ? null : 12345);
        var right = Media(rightId, kind, rightMissing ? null : 12345);
        return new Snapshot(
            SessionId: "sess-1",
            State: "ranking",
            Folder: folder,
            FolderName: folderName ?? Path.GetFileName(folder.TrimEnd('/')),
            Policy: kind,
            OpenedAt: "2026-09-17T00:00:00Z",
            PrefetchPairs: 2,
            SessionVotes: sessionVotes,
            Counts: new Counts(Total: unranked + 2, Rankable: unranked + 2, Unranked: unranked, Stills: kind == "still" ? unranked + 2 : 0, Videos: kind == "video" ? unranked + 2 : 0),
            Progress: 0.5,
            ProgressPercent: 50,
            Cues: cues ?? Array.Empty<string>(),
            Pair: new RankMaster2.Pc.Link.Wire.Pair(left, right),
            PairToken: pairToken,
            PairSeq: pairSeq,
            WarmPairs: warmPairs ?? Array.Empty<RankMaster2.Pc.Link.Wire.Pair>(),
            UndoAvailable: undoAvailable,
            LastAction: lastAction,
            LastSavedAt: null);
    }

    public static Snapshot Exhausted(string folder = "/lib", string? folderName = null, bool undoAvailable = false) =>
        Ranking(folder, folderName, undoAvailable: undoAvailable) with { State = "exhausted", Pair = null, PairToken = null };

    public static LastAction Action(
        string type,
        string? id = null,
        string? restoredId = null,
        string? undoneType = null,
        string? clientRequestId = "pc-req-1",
        string? pairToken = "tok-1",
        long seq = 1) =>
        new(seq, type, pairToken, clientRequestId, Winner: null, Side: null, Id: id, RestoredId: restoredId, UndoneType: undoneType, At: "2026-09-17T00:00:00Z");
}
