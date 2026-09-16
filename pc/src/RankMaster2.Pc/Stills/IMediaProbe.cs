using RankMaster2;

namespace RankMaster2.Pc.Stills;

// The pc/plans/C-stills.md section 2.2 seam, C & D -> A. No integrator-frozen file existed in this
// worktree when this part was built (see the note in RankMaster2.Pc.Stills.csproj), so this is the
// shape the plan argues for, taken verbatim from the plan text and PC_CLIENT_PARTS.md's ruling
// ("IMediaProbe splits in two, because it was two questions wearing one name" -- this is only the
// folder-level "does this folder contain video" half; per-file classification is
// Core.MediaExtensions.KindOf and is not a seam).

/// <summary>
/// "Does this folder contain video?" -- answered from filenames alone. Opens no file, decodes
/// nothing. Uses exactly the eligibility rules of <c>JsonCatalog.ListTopLevelMedia</c> so its
/// answer agrees with the server's policy: top level only; skip <c>rankmaster_db.json</c>,
/// <c>*.tmp</c>, Hidden and System attributes; <c>MediaExtensions.KindOf</c> decides the kind.
/// </summary>
public interface IMediaProbe
{
    Task<FolderMedia> ProbeAsync(string folder, CancellationToken ct);
}

public readonly record struct FolderMedia(int Stills, int Videos)
{
    public int Total => Stills + Videos;
    public bool HasVideo => Videos > 0;

    /// <summary>What the server will call `policy` for this folder: still unless there are videos and no stills.</summary>
    public MediaKind Policy => Videos > 0 && Stills == 0 ? MediaKind.Video : MediaKind.Still;

    /// <summary>The question A actually asks: will a session here ever play a frame?</summary>
    public bool NeedsVideoEngine => Policy == MediaKind.Video && Videos >= 2;
}
