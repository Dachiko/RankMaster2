using System.IO;
using RankMaster2.Catalog;
using RankMaster2.Ranking;

namespace RankMaster2;

/// <summary>Discard / special 1 / undo last move / rename-by-μ−3σ.</summary>
public sealed class LibraryActions : ILibraryActions
{
    private readonly Func<string> _folder;
    private readonly ICatalog _catalog;
    private readonly Func<IMediaPipeline> _pipeline;
    private readonly Func<RankingSession?> _session;
    private readonly Action<MediaId> _releaseUi;

    public LibraryActions(
        Func<string> folder,
        ICatalog catalog,
        Func<IMediaPipeline> pipeline,
        Func<RankingSession?> session,
        Action<MediaId> releaseUi)
    {
        _folder = folder;
        _catalog = catalog;
        _pipeline = pipeline;
        _session = session;
        _releaseUi = releaseUi;
    }

    public MovedFile? LastMove { get; private set; }

    public void Discard(MediaId id) => Move(id, FileOps.DiscardedFolder);

    public void MoveToSpecial(MediaId id) => Move(id, FileOps.SpecialFolder);

    public void ClearLastMove() => LastMove = null;

    public bool UndoLastMove()
    {
        if (LastMove is not { } move)
            return false;

        var session = _session();
        if (session is null || !string.Equals(move.Folder, session.Folder, StringComparison.OrdinalIgnoreCase))
        {
            LastMove = null;
            return false;
        }

        var destDir = Path.GetDirectoryName(move.SourcePath);
        if (string.IsNullOrEmpty(destDir))
            return false;

        // K14: never Directory.CreateDirectory here. This is the *media* folder — the one thing
        // JsonCatalog.Save was taught not to recreate, because a folder that is gone (an unplugged
        // drive, a folder renamed in Explorer) must look gone rather than be silently re-made empty.
        // Creating it would put back a directory and then fail the move into it anyway, leaving a
        // stray empty folder as the only trace.
        if (!Directory.Exists(destDir))
            throw new DirectoryNotFoundException("The folder this file came from is gone: " + destDir);

        var backName = FileOps.UniqueFileName(destDir, move.OriginalName);
        FileOps.MoveWithRetry(move.DestPath, Path.Combine(destDir, backName));

        var record = backName == move.OriginalName
            ? move.Record
            : move.Record with { Id = new MediaId(backName) };

        // Same reason, mirrored: the file is back, so this move is spent. Clearing afterwards left
        // a failed save holding an undo that could only ever throw on its second attempt.
        LastMove = null;
        _session()?.Restore(record);
        _session()?.Save();

        // The photograph has left the subfolder, so its row there is stale. A plain Save rebuilds
        // that database from what is actually present, which drops it - the same merge rule the
        // library folder has always used, applied to the folder the file just left. Best effort:
        // the rating is safely back in the library, and a stale row in discarded/ is untidiness,
        // not loss - the opposite of the carry itself, which is why only this half is swallowed.
        try
        {
            var subfolder = Path.Combine(move.Folder, move.Subfolder);
            if (Directory.Exists(subfolder))
                _catalog.Save(subfolder, _catalog.Scan(subfolder));
        }
        catch (Exception)
        {
        }

        return true;
    }

    public void RenameByRank()
    {
        var folder = _folder();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new InvalidOperationException("No folder to rename.");

        var records = _catalog.Scan(folder);
        if (records.Count == 0)
            throw new InvalidOperationException("Nothing to rename.");

        _catalog.Save(folder, records);
        _pipeline().ReleaseAll();
        var backup = FileOps.BackupLibrary(folder, DateTimeOffset.Now);
        try
        {
            var map = FileOps.RenameByConservativeScore(folder, records);
            var remapped = _catalog.RemapIds(records, map);
            _catalog.Save(folder, remapped);
            var session = _session();
            if (session is not null &&
                string.Equals(session.Folder, folder, StringComparison.OrdinalIgnoreCase))
                session.ReplaceAll(remapped);
        }
        catch (Exception ex)
        {
            try
            {
                FileOps.RestoreLibrary(folder, backup);
            }
            catch (Exception restoreEx)
            {
                throw new IOException(
                    $"Rename failed and restore did not finish. Backup is at:{Environment.NewLine}{backup}{Environment.NewLine}{restoreEx.Message}",
                    ex);
            }

            throw new IOException($"Rename failed. Folder restored from:{Environment.NewLine}{backup}", ex);
        }
    }

    private void Move(MediaId id, string subfolder)
    {
        var session = _session() ?? throw new InvalidOperationException("No session.");
        if (!session.TryFind(id, out var record))
            throw new InvalidOperationException("Unknown file: " + id.Filename);

        _releaseUi(id);
        _pipeline().Release(id);
        _pipeline().CancelWarmContaining(id);

        var destName = FileOps.MoveToSubfolder(session.Folder, id.Filename, subfolder);
        // Arm undo the moment the file physically moves, before anything that can throw. The save
        // below is not transactional: if it fails, the file is already in the subfolder and undo is
        // the only way back. Recording the move last left exactly that case unundoable.
        LastMove = new MovedFile(session.Folder, subfolder, id.Filename, destName, record);
        session.Drop(id);
        session.Save();
        CarryRatingInto(session.Folder, subfolder, destName, record);
    }

    /// <summary>
    /// Writes the moved file's rating into the subfolder's own <c>rankmaster_db.json</c>, so the
    /// judgement travels with the photograph instead of being deleted with its row.
    ///
    /// <para>The owner's reason (2026-09-17): he opens <c>discarded/</c> in Rank Master to check
    /// whether he threw something away by mistake, and <c>special 1/</c> is where his *best*
    /// pictures go — so those are exactly the files whose ratings cost the most comparisons to earn.
    /// Dropping the row meant a photograph moved back by hand returned as brand new, and the two
    /// subfolders were the only places in the product where ranking work was destroyed rather than
    /// kept.</para>
    ///
    /// <para>The file written is an ordinary v1 database in an ordinary folder, so Rank Master 2
    /// opens <c>discarded/</c> or <c>special 1/</c> and sees the ratings with no change to the
    /// schema — which is the constraint the owner fixed for this whole round.</para>
    ///
    /// <para>Runs <b>after</b> the top-level save, deliberately. The library's own database is the
    /// one that must be right; this is a second write to a second folder, and ordering it last means
    /// a failure here can never cost the main file. It is allowed to throw rather than being
    /// swallowed: a carry that silently did not happen is the very loss this exists to prevent, and
    /// the caller already treats a throw at this stage as "the file moved, the ranking file could
    /// not be written", which is true, with undo armed.</para>
    /// </summary>
    private void CarryRatingInto(string folder, string subfolder, string destName, MediaRecord record)
    {
        var target = Path.Combine(folder, subfolder);
        if (!Directory.Exists(target))
            return;

        var moved = new MediaId(destName);

        // Scan lists what is in the subfolder now - the new arrival included, with a default rating -
        // and keeps the ratings of everything already there. Replacing the one row is all that is
        // needed; Save is atomic, so a half-written subfolder database is not a state that exists.
        var rows = _catalog.Scan(target);
        var carried = new List<MediaRecord>(rows.Count);
        var replaced = false;
        foreach (var row in rows)
        {
            if (!replaced && row.Id == moved)
            {
                carried.Add(record with { Id = moved, Kind = row.Kind });
                replaced = true;
            }
            else
            {
                carried.Add(row);
            }
        }

        // The file is not in the listing: something moved or removed it between the move and now.
        // Nothing to carry, and inventing a row for a file that is not there would be worse.
        if (!replaced)
            return;

        _catalog.Save(target, carried);
    }
}
