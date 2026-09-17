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
    }
}
