namespace RankMaster2.Pc.Video;

using RankMaster2; // Core.MediaExtensions, MediaKind

/// <summary>D, C's share of the seam (PC_CLIENT_PARTS.md "Rulings"): the folder-level question that
/// keeps the video engine asleep. Never opens a file; classifies by extension only, delegating to
/// <see cref="RankMaster2.MediaExtensions"/> so this can never disagree with the server about what a
/// video is.</summary>
public enum FolderPolicy { Empty, Stills, Videos }

public interface IMediaProbe
{
    /// <summary>
    /// SPEC.md § Media policy by extension only, over the top-level files of <paramref name="folder"/>:
    /// only stills → Stills; only videos → Videos; both → Stills; nothing recognised → Empty.
    /// Never opens a file. Never throws for a missing or unreadable folder: returns Empty.
    /// </summary>
    FolderPolicy Classify(string folder);

    /// <summary>True iff the snapshot's policy is "video". The cheap answer when a snapshot exists.</summary>
    bool NeedsVideoEngine(string snapshotPolicy);
}

public sealed class MediaProbe : IMediaProbe
{
    public FolderPolicy Classify(string folder)
    {
        IEnumerable<string> files;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return FolderPolicy.Empty;
            files = Directory.EnumerateFiles(folder);
        }
        catch
        {
            // Unreadable folder (permissions, a race with deletion) — never throws.
            return FolderPolicy.Empty;
        }

        var sawStill = false;
        var sawVideo = false;
        try
        {
            foreach (var file in files)
            {
                var kind = MediaExtensions.KindOf(System.IO.Path.GetFileName(file));
                if (kind == MediaKind.Still)
                    sawStill = true;
                else if (kind == MediaKind.Video)
                    sawVideo = true;

                if (sawStill && sawVideo)
                    break; // both seen already resolves to Stills; nothing more to learn
            }
        }
        catch
        {
            return FolderPolicy.Empty;
        }

        if (!sawStill && !sawVideo)
            return FolderPolicy.Empty;
        if (sawVideo && !sawStill)
            return FolderPolicy.Videos;
        return FolderPolicy.Stills; // mixed folder: SPEC.md classifies it as stills
    }

    public bool NeedsVideoEngine(string snapshotPolicy) =>
        string.Equals(snapshotPolicy, "video", StringComparison.OrdinalIgnoreCase);
}
