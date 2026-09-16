using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RankMaster2.Ranking;

namespace RankMaster2.Catalog;

/// <summary>
/// The journal-based rename-by-rank engine for the server (SERVER_SPEC.md § 10.16, pc/plans/F-server-rename.md).
///
/// <para>The one fact everything here follows from: <see cref="JsonCatalog.Save"/> merges by filename
/// against what is on disk. Rename files without the database following and the very next ordinary
/// save silently empties every rating in the folder — indistinguishable from never having ranked at
/// all. So the journal is not a backup of the pixels (there is none — owner decision, no
/// <c>rankmaster_backup_*</c> folder); it is a durable copy of the one thing that cannot be
/// regenerated: which rating belongs to which file.</para>
///
/// <para><b>The journal.</b> <c>&lt;folder&gt;/.rankmaster-rename.json</c>, written and fsynced
/// before the first file moves. It carries the full old→new plan and every rating row, so recovery
/// can rebuild a correct database from the journal alone, independent of whatever
/// <c>rankmaster_db.json</c> currently says. Its extension is not a media extension, so
/// <see cref="JsonCatalog.Scan"/> never lists it.</para>
///
/// <para><b>Deterministic temporaries.</b> Phase 1 moves each <c>old</c> to <c>__rm2_&lt;new&gt;</c>;
/// phase 2 moves that to <c>&lt;new&gt;</c>. Every temporary names its own eventual target, so
/// recovery can identify any file it finds on disk without a per-file journal write — unlike the
/// desktop path's random-GUID temporaries, which a crash would leave unidentifiable.</para>
///
/// <para><b>Recovery is forward and total</b> (<see cref="RecoverIfPresent"/>): best-effort finish the
/// moves, then reunite every rating with its file at that file's *current* name, then delete the
/// journal. A half-renamed folder is an acceptable resting state (owner decision): recovery reunites
/// ratings even when it cannot finish every move. <see cref="ReuniteInPlace"/> is the same reunite
/// step alone, used by cancel (§ 3.5) — it deliberately does not finalize remaining moves.</para>
/// </summary>
public static class RenameEngine
{
    public const string JournalFileName = ".rankmaster-rename.json";
    public const string TempPrefix = "__rm2_";
    public const int JournalFormat = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string JournalPath(string folder) => Path.Combine(folder, JournalFileName);

    /// <summary>The deterministic temporary name a plan entry's file carries during phase 1 → phase 2.</summary>
    public static string TempNameOf(string newName) => TempPrefix + newName;

    // -----------------------------------------------------------------------------------------
    // The plan — SPEC.md "Rename by rank": μ − 3σ descending, then filename, to 000001.ext …
    // Identical ordering to FileOps.RenameByConservativeScore, restated here because the journal
    // needs the ratings alongside the mapping, not just old→new ids.
    // -----------------------------------------------------------------------------------------

    public static List<PlanEntry> BuildPlan(IReadOnlyList<MediaRecord> records)
    {
        var ordered = records
            .OrderByDescending(r => r.Rating.ConservativeScore)
            .ThenBy(r => r.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plan = new List<PlanEntry>(ordered.Count);
        var index = 1;
        foreach (var record in ordered)
        {
            var ext = Path.GetExtension(record.Filename);
            var newName = $"{index:D6}{ext}";
            plan.Add(new PlanEntry(
                record.Filename, newName, record.Rating.Mu, record.Rating.Sigma,
                record.Matches, record.Impressions, record.LastPlayed));
            index++;
        }

        return plan;
    }

    // -----------------------------------------------------------------------------------------
    // The journal — write+fsync before the first move (§ 3.2); read; delete (the commit point).
    // -----------------------------------------------------------------------------------------

    public static void WriteJournal(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt)
    {
        var dto = new RenameJournalDto(JournalFormat, "renaming", Rfc3339(createdAt), plan.ToList());
        var path = JournalPath(folder);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);   // fsync before anything moves — this is the safety property.
        }

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    public static bool JournalExists(string folder) => File.Exists(JournalPath(folder));

    public static RenameJournalDto? ReadJournal(string folder)
    {
        var path = JournalPath(folder);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RenameJournalDto>(json, JsonOptions);
    }

    public static void DeleteJournal(string folder)
    {
        var path = JournalPath(folder);
        if (File.Exists(path))
            File.Delete(path);
    }

    // -----------------------------------------------------------------------------------------
    // The moves — two-phase, within one directory, via the deterministic temporary (§ 3.2, § 3.3).
    // `shouldStop` is polled before each move so the live operation can be cancelled between files,
    // and so a test can drive the executor to an exact interruption point deterministically instead
    // of racing a real process kill.
    // -----------------------------------------------------------------------------------------

    /// <summary>old → __rm2_&lt;new&gt;. Returns the number of files actually moved.</summary>
    public static int MovePhase1(
        string folder,
        IReadOnlyList<PlanEntry> plan,
        Func<int, bool>? shouldStop = null,
        Action<int, int>? onProgress = null)
    {
        var done = 0;
        foreach (var entry in plan)
        {
            if (shouldStop?.Invoke(done) == true)
                break;

            var source = Path.Combine(folder, entry.Old);
            var dest = Path.Combine(folder, TempNameOf(entry.New));
            if (File.Exists(source))
                FileOps.MoveWithRetry(source, dest);

            done++;
            onProgress?.Invoke(done, plan.Count);
        }

        return done;
    }

    /// <summary>__rm2_&lt;new&gt; → new. Returns the number of files actually moved.</summary>
    public static int MovePhase2(
        string folder,
        IReadOnlyList<PlanEntry> plan,
        Func<int, bool>? shouldStop = null,
        Action<int, int>? onProgress = null)
    {
        var done = 0;
        foreach (var entry in plan)
        {
            if (shouldStop?.Invoke(done) == true)
                break;

            var source = Path.Combine(folder, TempNameOf(entry.New));
            var dest = Path.Combine(folder, entry.New);
            if (File.Exists(source))
                FileOps.MoveWithRetry(source, dest);

            done++;
            onProgress?.Invoke(done, plan.Count);
        }

        return done;
    }

    /// <summary>Writes the database with the plan's new keys, atomically (§ 3.3 step "saving").</summary>
    public static IReadOnlyList<MediaRecord> Commit(
        ICatalog catalog, string folder, IReadOnlyList<MediaRecord> preRenameRecords, IReadOnlyList<PlanEntry> plan)
    {
        var map = plan.ToDictionary(
            e => new MediaId(e.Old), e => new MediaId(e.New));
        var remapped = catalog.RemapIds(preRenameRecords, map);
        catalog.Save(folder, remapped);
        return remapped;
    }

    // -----------------------------------------------------------------------------------------
    // Recovery — forward, automatic, total (§ 3.3). Runs on the next POST /session, before any
    // scan, whenever the journal is found. This is the heart of the whole design: every
    // interruption point must resolve to "every rating finds its file."
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Best-effort finish the moves toward <c>new</c>, then reunite every rating with its file at
    /// that file's current name, then delete the journal. Never throws for a partially-renamed
    /// folder — a half-renamed folder is an acceptable resting state (owner decision 4); the only
    /// failure this reports is the reunite <c>Save</c> itself failing (e.g. disk full), in which
    /// case the journal is left for the next open to retry.
    /// </summary>
    public static RenameRecoveryOutcome RecoverIfPresent(string folder, ICatalog catalog)
    {
        var journal = ReadJournal(folder);
        if (journal is null)
            return new RenameRecoveryOutcome(JournalWasPresent: false, Reunited: true, JournalPath: null);

        FinalizeBestEffort(folder, journal.Plan);
        var reunited = Reunite(folder, journal.Plan, catalog);

        if (reunited)
        {
            DeleteJournal(folder);
            return new RenameRecoveryOutcome(true, true, null);
        }

        return new RenameRecoveryOutcome(true, false, JournalPath(folder));
    }

    /// <summary>
    /// Cancel's "stop and reunite in place" (§ 3.5): the same reunite step alone, deliberately
    /// without finalizing the moves still outstanding. The folder is left exactly as it stood the
    /// moment the run stopped issuing moves — possibly with mixed <c>old</c>/temp/<c>new</c> names —
    /// and every rating is reunited with whichever name its file currently carries.
    /// </summary>
    public static RenameRecoveryOutcome ReuniteInPlace(string folder, ICatalog catalog)
    {
        var journal = ReadJournal(folder);
        if (journal is null)
            return new RenameRecoveryOutcome(JournalWasPresent: false, Reunited: true, JournalPath: null);

        var reunited = Reunite(folder, journal.Plan, catalog);

        if (reunited)
        {
            DeleteJournal(folder);
            return new RenameRecoveryOutcome(true, true, null);
        }

        return new RenameRecoveryOutcome(true, false, JournalPath(folder));
    }

    /// <summary>
    /// For each plan entry, move whatever is on disk one step closer to <c>new</c>
    /// (<c>old</c> → temp → <c>new</c>), skipping any entry whose file is entirely gone and
    /// swallowing any failure per-entry — finalizing is best-effort by design (§ 3.3): a file that
    /// cannot be finished moving is untidy, never lossy, because <see cref="Reunite"/> finds it
    /// afterwards under whichever name it actually has.
    /// </summary>
    private static void FinalizeBestEffort(string folder, IReadOnlyList<PlanEntry> plan)
    {
        foreach (var entry in plan)
        {
            try
            {
                var oldPath = Path.Combine(folder, entry.Old);
                var tempPath = Path.Combine(folder, TempNameOf(entry.New));
                var newPath = Path.Combine(folder, entry.New);

                if (File.Exists(newPath))
                    continue;

                if (File.Exists(tempPath))
                {
                    FileOps.MoveWithRetry(tempPath, newPath);
                    continue;
                }

                if (File.Exists(oldPath))
                {
                    FileOps.MoveWithRetry(oldPath, tempPath);
                    FileOps.MoveWithRetry(tempPath, newPath);
                }

                // Neither old, temp nor new is present: the file is gone. Not this routine's
                // failure to fix — Reunite simply has no row for it, and that rating is the one
                // honest loss the design accepts (the image itself no longer exists either).
            }
            catch (IOException)
            {
                // Best effort: leave this entry at whatever name it holds. Reunite still finds it.
            }
        }
    }

    /// <summary>
    /// The step that matters (§ 1): list every media file actually on disk right now, and for each,
    /// recover its rating from the journal by matching whichever of <c>new</c>, the deterministic
    /// temp, or <c>old</c> the file currently bears. A file with no journal entry gets a fresh
    /// default rating, exactly as <see cref="JsonCatalog.Scan"/> already does for an unknown file. A
    /// journal entry whose file is gone from disk entirely is dropped — its image no longer exists,
    /// so there is nothing to reunite it with. The result is written with <see cref="ICatalog.Save"/>,
    /// which is atomic, so this step can never leave a half-written database on disk.
    /// </summary>
    private static bool Reunite(string folder, IReadOnlyList<PlanEntry> plan, ICatalog catalog)
    {
        var byNew = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
        var byTemp = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
        var byOld = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan)
        {
            byNew[entry.New] = entry;
            byTemp[TempNameOf(entry.New)] = entry;
            byOld[entry.Old] = entry;
        }

        var onDisk = JsonCatalog.ListTopLevelMedia(folder);
        var records = new List<MediaRecord>(onDisk.Count);
        foreach (var (id, kind) in onDisk)
        {
            PlanEntry? found =
                byNew.TryGetValue(id.Filename, out var viaNew) ? viaNew :
                byTemp.TryGetValue(id.Filename, out var viaTemp) ? viaTemp :
                byOld.TryGetValue(id.Filename, out var viaOld) ? viaOld :
                null;

            records.Add(found is { } entry
                ? new MediaRecord(id, kind, new Rating(entry.Mu, entry.Sigma), entry.Matches, entry.Impressions, entry.LastPlayed)
                : new MediaRecord(id, kind, RankingConstants.DefaultRating, 0, 0, 0));
        }

        try
        {
            catalog.Save(folder, records);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Rfc3339(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>One row of the journal's plan: an old→new move plus the rating it must arrive with.</summary>
public sealed record PlanEntry(
    [property: JsonPropertyName("old")] string Old,
    [property: JsonPropertyName("new")] string New,
    [property: JsonPropertyName("mu")] double Mu,
    [property: JsonPropertyName("sigma")] double Sigma,
    [property: JsonPropertyName("matches")] int Matches,
    [property: JsonPropertyName("impressions")] int Impressions,
    [property: JsonPropertyName("lastPlayed")] long LastPlayed);

/// <summary>The on-disk shape of <c>.rankmaster-rename.json</c> (SERVER_SPEC.md § 10.16).</summary>
public sealed record RenameJournalDto(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("plan")] List<PlanEntry> Plan);

/// <summary>
/// What <see cref="RenameEngine.RecoverIfPresent"/> / <see cref="RenameEngine.ReuniteInPlace"/> did.
/// <c>Reunited</c> true means every rating is safe (the folder may still be half-renamed).
/// <c>Reunited</c> false is the rare double fault — the reunite <c>Save</c> itself failed — and
/// <c>JournalPath</c> names the journal left behind for the next open to retry.
/// </summary>
public readonly record struct RenameRecoveryOutcome(bool JournalWasPresent, bool Reunited, string? JournalPath);
