using System.Globalization;
using System.Security.Cryptography;
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
/// <para><b>The per-run suffix</b> (<c>000001-a3f9.jpg</c>) is what makes identifying a file by its
/// name sound at all. Recovery asks of every file on disk: is this name a <c>new</c> name or an
/// <c>old</c> one? That question has one answer only if those two sets are disjoint — and without a
/// suffix they are not. A folder renamed once is full of <c>000001.jpg</c>…, so a second rename
/// plans moves in which one entry's <c>old</c> is another entry's <c>new</c>; an interrupted run
/// then leaves files whose names identify them as two different pictures at once, and recovery
/// hands ratings to the wrong images. This is not a corner case: <c>000001.jpg</c> is exactly what
/// Rank Master 2's own rename produces, so the owner's folders already contain those names.
/// A suffix drawn fresh per run, and checked against every name already in the folder, makes a new
/// name something that provably cannot already exist there — so every file on disk belongs to
/// exactly one plan entry, at every interruption point, on every rename after the first.
/// The index keeps its leading zeroes and the suffix is identical for every file of a run, so the
/// folder still sorts by rank, which is the whole point of renaming.</para>
///
/// <para><b>One move per file.</b> <c>old → new</c>, directly: there is no temporary name and no
/// second phase. A two-phase move exists to break a cycle between old and new names, and the run
/// suffix has already proven there is no such cycle. The move never overwrites — a destination that
/// exists is a thrown <see cref="IOException"/>, which fails the run safely, because the journal is
/// still on disk and the ratings are reunited from it. (The frozen desktop app's
/// <see cref="FileOps.RenameByConservativeScore"/> keeps its own random-GUID temporaries; it is a
/// different program and is deliberately untouched.)</para>
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

    /// <summary>The format this engine writes. Format 1 (the same shape without <c>suffix</c>) is still read.</summary>
    public const int JournalFormat = 2;

    /// <summary>
    /// Every name one run produces (SERVER_SPEC.md § 10.16, SPEC.md § Rename by rank). Six digits of
    /// rank first, so a byte-wise sort of the names is rank order; then the run's four hex characters.
    /// </summary>
    public const string NamePattern = @"^\d{6}-[0-9a-f]{4}\.[^.]+$";

    /// <summary>How many suffixes are drawn before the run gives up (§ 1.1: sixteen, then fail).</summary>
    private const int SuffixDraws = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string JournalPath(string folder) => Path.Combine(folder, JournalFileName);

    // -----------------------------------------------------------------------------------------
    // The plan — SPEC.md "Rename by rank": μ − 3σ descending, then filename, to
    // 000001-<suffix>.ext … Same ordering as FileOps.RenameByConservativeScore (the desktop path,
    // which keeps its unsuffixed names and is deliberately left alone), restated here because the
    // journal needs the ratings alongside the mapping, not just old→new ids.
    // -----------------------------------------------------------------------------------------

    /// <summary>The name a file of this rank gets in a run identified by <paramref name="runSuffix"/>.</summary>
    public static string NewNameOf(int index, string runSuffix, string extension) =>
        $"{index:D6}-{runSuffix}{extension}";

    /// <summary>
    /// Builds the old→new plan, verifying the run suffix against the input names only. Prefer the
    /// overload that takes the folder: it also checks the files on disk that no record mentions.
    /// </summary>
    public static List<PlanEntry> BuildPlan(
        IReadOnlyList<MediaRecord> records,
        string? runSuffix = null,
        Func<string>? drawSuffix = null) =>
        BuildPlanCore(records, Array.Empty<string>(), runSuffix, drawSuffix);

    /// <summary>
    /// Builds the old→new plan for <paramref name="folder"/>, verifying the run suffix against the
    /// input names <b>and</b> every top-level media file the folder currently lists — including files
    /// the session never knew about. <paramref name="runSuffix"/> is normally left null and a fresh
    /// one is drawn; passing one explicitly is for tests that need a predictable name.
    /// <paramref name="drawSuffix"/> replaces the random source, which is how a test forces the
    /// collision that proves the redraw; nothing but a test ever passes it.
    /// </summary>
    public static List<PlanEntry> BuildPlan(
        string folder,
        IReadOnlyList<MediaRecord> records,
        string? runSuffix = null,
        Func<string>? drawSuffix = null) =>
        BuildPlanCore(
            records,
            JsonCatalog.ListTopLevelMedia(folder).Select(m => m.Id.Filename).ToList(),
            runSuffix,
            drawSuffix);

    private static List<PlanEntry> BuildPlanCore(
        IReadOnlyList<MediaRecord> records,
        IReadOnlyList<string> alsoOnDisk,
        string? runSuffix,
        Func<string>? drawSuffix)
    {
        var ordered = records
            .OrderByDescending(r => r.Rating.ConservativeScore)
            .ThenBy(r => r.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var suffix = runSuffix ?? ChooseRunSuffix(
            ordered.Select(r => r.Filename).Concat(alsoOnDisk),
            drawSuffix ?? (() => RandomSuffix()));

        var plan = new List<PlanEntry>(ordered.Count);
        var index = 1;
        foreach (var record in ordered)
        {
            var newName = NewNameOf(index, suffix, Path.GetExtension(record.Filename));
            plan.Add(new PlanEntry(
                record.Filename, newName, record.Rating.Mu, record.Rating.Sigma,
                record.Matches, record.Impressions, record.LastPlayed));
            index++;
        }

        RequireDisjoint(plan,
            "A rename plan was built in which a new name is also an old name. The run suffix is " +
            "verified against the folder precisely so this cannot happen, so this is a bug in the " +
            "engine, never a condition of the folder.");

        return plan;
    }

    /// <summary>
    /// Draws a run suffix that no name in play already ends with. The rule is one sentence: a
    /// candidate <c>ssss</c> is rejected while any name — a record's or a file's — <b>ends in
    /// <c>-ssss</c> once its extension is removed</b>, whatever its rank or stem. That is stronger
    /// than checking only the names this plan would produce, and it is what makes "the old names and
    /// the new names of one run are disjoint sets" true by construction rather than by luck.
    ///
    /// <para>Sixteen rejections in a row means something is wrong with the folder or the draw, and
    /// the honest answer is to fail the run before a single file has moved — never to widen the
    /// suffix, which would produce names of two shapes in one library.</para>
    /// </summary>
    private static string ChooseRunSuffix(IEnumerable<string> namesInPlay, Func<string> draw)
    {
        var stems = namesInPlay
            .Select(name => Path.GetFileNameWithoutExtension(name) ?? string.Empty)
            .Where(stem => stem.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var attempt = 0; attempt < SuffixDraws; attempt++)
        {
            var candidate = draw();
            var marker = "-" + candidate;
            if (!stems.Any(stem => stem.EndsWith(marker, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        throw new InvalidOperationException(
            $"Could not draw a rename suffix this folder does not already use in {SuffixDraws} " +
            "attempts. Nothing has been moved and no rating has changed.");
    }

    private static string RandomSuffix() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    /// <summary>
    /// The property recovery rests on: no <c>new</c> name is any entry's <c>old</c> name. Checked
    /// when a plan is built (a violation is a bug) and again when one is read back from a journal
    /// (a violation is a hand-edited file, and the journal is then unreadable).
    /// </summary>
    private static bool IsDisjoint(IReadOnlyList<PlanEntry> plan)
    {
        var olds = new HashSet<string>(plan.Select(e => e.Old), StringComparer.OrdinalIgnoreCase);
        return !plan.Any(e => olds.Contains(e.New));
    }

    private static void RequireDisjoint(IReadOnlyList<PlanEntry> plan, string because)
    {
        if (!IsDisjoint(plan))
            throw new InvalidOperationException(because);
    }

    /// <summary>The run suffix a plan carries, read back off its first new name; null for an empty plan.</summary>
    public static string? SuffixOf(IReadOnlyList<PlanEntry> plan)
    {
        if (plan.Count == 0)
            return null;

        var stem = Path.GetFileNameWithoutExtension(plan[0].New);
        var hyphen = stem.LastIndexOf('-');
        return hyphen >= 0 ? stem[(hyphen + 1)..] : null;
    }

    // -----------------------------------------------------------------------------------------
    // The journal — write+fsync before the first move (§ 3.2); read; delete (the commit point).
    // -----------------------------------------------------------------------------------------

    public static void WriteJournal(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt)
    {
        var dto = new RenameJournalDto(
            JournalFormat, "renaming", Rfc3339(createdAt), SuffixOf(plan), plan.ToList());
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

        // Delete-then-move rather than File.Replace: the destination is hidden on Windows (below),
        // and replacing a hidden file with a visible one is the kind of attribute-juggling that
        // fails on some volumes for no benefit. Both calls are directory-entry operations, and the
        // fsynced content is already safe on disk under the temporary name.
        if (File.Exists(path))
            File.Delete(path);
        File.Move(tmp, path);

        Hide(path);
    }

    /// <summary>
    /// K7: the journal is the server's bookkeeping, not the owner's picture, and it sits in his photo
    /// folder. On Windows that means hidden, so Explorer does not show him a file he might delete
    /// mid-rename. No-op elsewhere, where a leading dot already says the same thing.
    /// </summary>
    private static void Hide(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tidiness, never safety: a journal that could not be hidden still protects the ratings.
        }
    }

    public static bool JournalExists(string folder) => File.Exists(JournalPath(folder));

    /// <summary>
    /// Reads the journal, or returns null if there is none. A journal that is present but cannot be
    /// trusted — a format this engine does not know, a missing or null <c>plan</c>, malformed or
    /// truncated JSON, or a plan whose <c>old</c> and <c>new</c> sets intersect — throws
    /// <see cref="InvalidDataException"/>. It is never guessed at: the server turns that exception
    /// into <c>rename_failed { reunited:false, journal }</c> and releases the folder lock
    /// (SERVER_SPEC.md § 10.16), which tells the owner the truth and leaves the file for him to look
    /// at. Guessing would mean writing a database from a plan that cannot say which file is which.
    /// </summary>
    public static RenameJournalDto? ReadJournal(string folder)
    {
        var path = JournalPath(folder);
        if (!File.Exists(path))
            return null;

        RenameJournalDto? journal;
        try
        {
            journal = JsonSerializer.Deserialize<RenameJournalDto>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{JournalFileName} is not readable JSON.", e);
        }

        if (journal is null)
            throw new InvalidDataException($"{JournalFileName} holds no journal object.");

        if (journal.Format is not (1 or JournalFormat))
            throw new InvalidDataException(
                $"{JournalFileName} is format {journal.Format}; this server reads 1 and {JournalFormat}.");

        if (journal.Plan is null)
            throw new InvalidDataException($"{JournalFileName} has no plan.");

        if (!IsDisjoint(journal.Plan))
            throw new InvalidDataException(
                $"{JournalFileName} has a plan in which a new name is also an old name, so a file on " +
                "disk cannot be identified. A journal this engine wrote can never say that.");

        return journal;
    }

    public static void DeleteJournal(string folder)
    {
        var path = JournalPath(folder);
        if (File.Exists(path))
            File.Delete(path);
    }

    // -----------------------------------------------------------------------------------------
    // The moves — one per file, old → new, within one directory (§ 10.16 "One move per file").
    // `shouldStop` is polled before each move so the live operation can be cancelled between files,
    // and so a test can drive the executor to an exact interruption point deterministically instead
    // of racing a real process kill.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// old → new, one move per file. Returns the number of entries it got through.
    ///
    /// <para>Cancel lands within one retry interval, not after a whole retry budget: a move waiting
    /// on a file another program holds is retried, and <paramref name="shouldStop"/> is polled
    /// between those retries, throwing <see cref="OperationCanceledException"/> the moment it fires.
    /// The owner's reason for the Cancel button is a slow USB drive (SERVER_SPEC.md § 10.16, "Why
    /// there is a bar and a cancel"), where a button that answers after the whole budget is a button
    /// that appears frozen.</para>
    /// </summary>
    public static int MoveAll(
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
            var dest = Path.Combine(folder, entry.New);
            if (File.Exists(source))
            {
                var at = done;
                FileOps.MoveWithRetry(source, dest, () => shouldStop?.Invoke(at) == true);
            }

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
    /// case the journal is left for the next open to retry. An unreadable journal is a different
    /// thing entirely and throws <see cref="InvalidDataException"/> (see <see cref="ReadJournal"/>).
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
    /// moment the run stopped issuing moves — a mixture of <c>old</c> and <c>new</c> names — and
    /// every rating is reunited with whichever name its file currently carries.
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
    /// For each plan entry: if <c>new</c> is there the move is done; else if <c>old</c> is there,
    /// move it. Any failure is swallowed per entry — finalizing is best-effort by design (§ 3.3): a
    /// file that cannot be finished moving is untidy, never lossy, because <see cref="Reunite"/>
    /// finds it afterwards under whichever name it actually has.
    /// </summary>
    private static void FinalizeBestEffort(string folder, IReadOnlyList<PlanEntry> plan)
    {
        foreach (var entry in plan)
        {
            try
            {
                var oldPath = Path.Combine(folder, entry.Old);
                var newPath = Path.Combine(folder, entry.New);

                if (File.Exists(newPath))
                    continue;

                if (File.Exists(oldPath))
                    FileOps.MoveWithRetry(oldPath, newPath);

                // Neither old nor new is present: the file is gone. Not this routine's failure to
                // fix — Reunite simply has no row for it, and that rating is the one honest loss the
                // design accepts (the image itself no longer exists either).
            }
            catch (Exception)
            {
                // Best effort: leave this entry at whatever name it holds. Reunite still finds it.
                // Deliberately catches every exception, not just IOException: an access-denied move
                // (UnauthorizedAccessException — an ACL-denied file or folder, the realistic Windows
                // trigger) must be swallowed exactly like a locked file, or this "any failure is
                // swallowed" promise is false for the one case that matters most, and the exception
                // walks out of here, out of RecoverIfPresent, and — since this runs from inside
                // SessionRegistry's own catch block — out of the rename task entirely, wedging the
                // session until a restart (AUDIT2.md § 2.2).
            }
        }
    }

    /// <summary>
    /// The step that matters (§ 1): list every media file actually on disk right now, and for each,
    /// recover its rating from the journal by asking whether its name is a <c>new</c> name or an
    /// <c>old</c> one. The two sets are disjoint (the run suffix was verified against the folder
    /// before the plan was used, and <see cref="ReadJournal"/> re-checks it), so the first hit is the
    /// only possible hit and no file is ambiguous. A file with no journal entry gets a fresh default
    /// rating, exactly as <see cref="JsonCatalog.Scan"/> already does for an unknown file. A journal
    /// entry whose file is gone from disk entirely is dropped — its image no longer exists, so there
    /// is nothing to reunite it with. The result is written with <see cref="ICatalog.Save"/>, which
    /// is atomic, so this step can never leave a half-written database on disk.
    /// </summary>
    private static bool Reunite(string folder, IReadOnlyList<PlanEntry> plan, ICatalog catalog)
    {
        var byNew = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
        var byOld = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan)
        {
            byNew[entry.New] = entry;
            byOld[entry.Old] = entry;
        }

        var onDisk = JsonCatalog.ListTopLevelMedia(folder);
        var records = new List<MediaRecord>(onDisk.Count);
        foreach (var (id, kind) in onDisk)
        {
            PlanEntry? found =
                byNew.TryGetValue(id.Filename, out var viaNew) ? viaNew :
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

/// <summary>
/// The on-disk shape of <c>.rankmaster-rename.json</c> (SERVER_SPEC.md § 10.16). Format 2 adds
/// <c>suffix</c>, the four hex characters every name of this run carries; format 1 is the same
/// shape without it and is still read.
/// </summary>
public sealed record RenameJournalDto(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("suffix")] string? Suffix,
    [property: JsonPropertyName("plan")] List<PlanEntry> Plan);

/// <summary>
/// What <see cref="RenameEngine.RecoverIfPresent"/> / <see cref="RenameEngine.ReuniteInPlace"/> did.
/// <c>Reunited</c> true means every rating is safe (the folder may still be half-renamed).
/// <c>Reunited</c> false is the rare double fault — the reunite <c>Save</c> itself failed — and
/// <c>JournalPath</c> names the journal left behind for the next open to retry.
/// </summary>
public readonly record struct RenameRecoveryOutcome(bool JournalWasPresent, bool Reunited, string? JournalPath);
