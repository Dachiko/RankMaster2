using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Catalog.Tests;

/// <summary>
/// The acceptance gate for pc/plans/F-server-rename.md (§ 8): a crash at any step of the rename
/// resolves, after recovery, to "every rating still finds its file." Every interruption is driven
/// deterministically — build the plan, run exactly as far as the step names, stop, and inspect the
/// folder as a fresh process would find it — rather than racing a real process kill, which the plan
/// argues is the stronger test because it covers every step, not one lucky moment.
/// </summary>
public class RenameEngineTests
{
    // ---- the crash matrix (SERVER_SPEC.md § 10.16, plan § 3.3) ---------------------------------

    public static IEnumerable<object[]> InterruptionPoints() => new[]
    {
        new object[] { "after journal fsync, before any move" },
        new object[] { "mid phase 1" },
        new object[] { "mid phase 2" },
        new object[] { "after all moves, before the DB save" },
        new object[] { "after the DB save, before the journal delete" },
    };

    [Theory]
    [MemberData(nameof(InterruptionPoints))]
    public void Every_rating_finds_its_file_after_a_crash_at_each_step(string step)
    {
        var dir = Temp();
        var catalog = new JsonCatalog();

        var records = new List<MediaRecord>
        {
            Rec("alpha.jpg", mu: 30, sigma: 1, matches: 9, impressions: 9, lastPlayed: 111),
            Rec("bravo.jpg", mu: 25, sigma: 2, matches: 4, impressions: 5, lastPlayed: 222),
            Rec("charlie.jpg", mu: 20, sigma: 3, matches: 1, impressions: 1, lastPlayed: 333),
        };
        foreach (var r in records)
            File.WriteAllBytes(Path.Combine(dir, r.Filename), new byte[] { 1, 2, 3 });

        var originalRatings = records.ToDictionary(r => r.Filename, r => r.Rating, StringComparer.OrdinalIgnoreCase);
        var originalCounters = records.ToDictionary(
            r => r.Filename, r => (r.Matches, r.Impressions, r.LastPlayed), StringComparer.OrdinalIgnoreCase);

        var plan = RenameEngine.BuildPlan(records);
        Assert.Equal("alpha.jpg", plan[0].Old);              // highest conservative score
        Assert.StartsWith("000001-", plan[0].New, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", plan[0].New, StringComparison.Ordinal);

        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        Assert.True(RenameEngine.JournalExists(dir), "the journal must exist before any move (§ 3.2)");

        switch (step)
        {
            case "after journal fsync, before any move":
                break;   // stop here: nothing moved yet

            case "mid phase 1":
                RenameEngine.MovePhase1(dir, plan, shouldStop: done => done >= 1);
                break;

            case "mid phase 2":
                RenameEngine.MovePhase1(dir, plan);
                RenameEngine.MovePhase2(dir, plan, shouldStop: done => done >= 1);
                break;

            case "after all moves, before the DB save":
                RenameEngine.MovePhase1(dir, plan);
                RenameEngine.MovePhase2(dir, plan);
                // The dangerous moment (§ 1): every file has its new name and nothing has told the
                // database yet. This folder has no rankmaster_db.json at all, which is the harshest
                // version of that danger — recovery must still work with no on-disk DB to lean on;
                // the dedicated test below covers the case where a stale DB is actively misleading.
                break;

            case "after the DB save, before the journal delete":
                RenameEngine.MovePhase1(dir, plan);
                RenameEngine.MovePhase2(dir, plan);
                RenameEngine.Commit(catalog, dir, records, plan);
                break;

            default:
                throw new InvalidOperationException(step);
        }

        // A fresh process opens the folder: recovery runs before anything else.
        var outcome = RenameEngine.RecoverIfPresent(dir, catalog);
        Assert.True(outcome.Reunited, $"[{step}] recovery must reunite every rating (§ 10.16).");
        Assert.False(RenameEngine.JournalExists(dir), $"[{step}] a successful recovery deletes the journal.");

        var loaded = catalog.Scan(dir).ToDictionary(r => r.Filename, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, loaded.Count);

        // Every original rating must be found under whichever id carries it now — matched through
        // the plan, since the filename itself may have changed.
        foreach (var entry in plan)
        {
            var expectedRating = originalRatings[entry.Old];
            var expectedCounters = originalCounters[entry.Old];

            // The file could legitimately be at `old` (nothing moved yet) or `new` (fully moved);
            // recovery reunites at whichever name is actually on disk.
            var currentName = File.Exists(Path.Combine(dir, entry.New)) ? entry.New : entry.Old;
            Assert.True(loaded.TryGetValue(currentName, out var record),
                $"[{step}] no record found for '{currentName}' (plan: {entry.Old} -> {entry.New}).");

            Assert.Equal(expectedRating.Mu, record!.Rating.Mu);
            Assert.Equal(expectedRating.Sigma, record.Rating.Sigma);
            Assert.Equal(expectedCounters.Matches, record.Matches);
            Assert.Equal(expectedCounters.Impressions, record.Impressions);
            Assert.Equal(expectedCounters.LastPlayed, record.LastPlayed);
        }
    }

    /// <summary>
    /// § 1, stated as a test: the dangerous moment is files renamed, database not — because the
    /// *next ordinary save* through the plain `Scan`/merge path would silently drop every rating.
    /// This constructs that exact state by hand (a real `rankmaster_db.json` with the *old* keys,
    /// files already at their *new* names) and proves both halves: the plain merge loses the
    /// ratings, and `RecoverIfPresent` followed by `Scan` does not.
    /// </summary>
    [Fact]
    public void Recovery_runs_before_scan_and_does_not_lose_ratings_to_the_merge()
    {
        var dir = Temp();
        var catalog = new JsonCatalog();

        var records = new List<MediaRecord>
        {
            Rec("alpha.jpg", mu: 30, sigma: 1, matches: 9, impressions: 9, lastPlayed: 111),
            Rec("bravo.jpg", mu: 25, sigma: 2, matches: 4, impressions: 5, lastPlayed: 222),
        };
        foreach (var r in records)
            File.WriteAllBytes(Path.Combine(dir, r.Filename), new byte[] { 1, 2, 3 });

        // A real database exists, naming the OLD files (as if the session had saved before renaming).
        catalog.Save(dir, records);

        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        RenameEngine.MovePhase1(dir, plan);
        RenameEngine.MovePhase2(dir, plan);

        // The files are all renamed now; rankmaster_db.json still lists alpha.jpg / bravo.jpg.
        Assert.True(File.Exists(Path.Combine(dir, plan[0].New)));
        Assert.False(File.Exists(Path.Combine(dir, "alpha.jpg")));

        // Half 1: the catastrophe. A plain Scan+Save (what an ordinary vote would do) assigns the
        // renamed files FRESH ratings, because their names match no row in the stale database.
        var scannedWithoutRecovery = catalog.Scan(dir);
        foreach (var record in scannedWithoutRecovery)
        {
            Assert.Equal(RankingConstants.DefaultRating, record.Rating);
            Assert.Equal(0, record.Matches);
        }

        // Half 2: recovery, run first, prevents exactly that.
        var outcome = RenameEngine.RecoverIfPresent(dir, catalog);
        Assert.True(outcome.Reunited);

        var recovered = catalog.Scan(dir).ToDictionary(r => r.Filename, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(30, recovered[plan[0].New].Rating.Mu);
        Assert.Equal(9, recovered[plan[0].New].Matches);
        Assert.Equal(25, recovered[plan[1].New].Rating.Mu);
        Assert.Equal(4, recovered[plan[1].New].Matches);
    }

    // ---- cancel: stop and reunite in place, never a rollback ------------------------------------

    [Fact]
    public void Cancel_in_place_leaves_a_valid_half_renamed_folder()
    {
        var dir = Temp();
        var catalog = new JsonCatalog();

        var records = new List<MediaRecord>
        {
            Rec("alpha.jpg", mu: 30, sigma: 1, matches: 9, impressions: 9, lastPlayed: 111),
            Rec("bravo.jpg", mu: 25, sigma: 2, matches: 4, impressions: 5, lastPlayed: 222),
            Rec("charlie.jpg", mu: 20, sigma: 3, matches: 1, impressions: 1, lastPlayed: 333),
        };
        foreach (var r in records)
            File.WriteAllBytes(Path.Combine(dir, r.Filename), new byte[] { 1, 2, 3 });

        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        // Stop mid-renaming: one file finished (phase 1 + phase 2), the rest untouched.
        RenameEngine.MovePhase1(dir, plan, shouldStop: done => done >= 1);
        RenameEngine.MovePhase2(dir, plan, shouldStop: done => done >= 1);

        // A genuinely mixed folder: some original names, some new.
        var onDiskNames = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name != RenameEngine.JournalFileName)
            .ToHashSet();
        Assert.Contains(plan[0].New, onDiskNames);         // finished
        Assert.Contains(plan[1].Old, onDiskNames);          // untouched
        Assert.Contains(plan[2].Old, onDiskNames);          // untouched

        var outcome = RenameEngine.ReuniteInPlace(dir, catalog);
        Assert.True(outcome.Reunited);
        Assert.False(RenameEngine.JournalExists(dir), "cancel deletes the journal once reunited.");

        // ReuniteInPlace must NOT have finalized the outstanding moves — that is what distinguishes
        // it from recovery. The media files stay exactly as mixed as they were; only the database
        // (rewritten atomically) and the journal (deleted) are allowed to change.
        var afterMediaNames = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name != JsonCatalog.FileName)
            .ToHashSet();
        Assert.Equal(onDiskNames, afterMediaNames);

        var loaded = catalog.Scan(dir).ToDictionary(r => r.Filename, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(30, loaded[plan[0].New].Rating.Mu);
        Assert.Equal(25, loaded[plan[1].Old].Rating.Mu);
        Assert.Equal(20, loaded[plan[2].Old].Rating.Mu);
    }

    // ---- the journal itself -----------------------------------------------------------------

    [Fact]
    public void The_journal_is_ignored_by_scan_and_carries_the_ratings()
    {
        var dir = Temp();
        var catalog = new JsonCatalog();
        var records = new List<MediaRecord> { Rec("alpha.jpg", mu: 30, sigma: 1) };
        File.WriteAllBytes(Path.Combine(dir, "alpha.jpg"), new byte[] { 1 });

        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        // Scan must never list the journal file as a media record.
        var scanned = catalog.Scan(dir);
        Assert.Single(scanned);
        Assert.Equal("alpha.jpg", scanned[0].Filename);

        var journal = RenameEngine.ReadJournal(dir);
        Assert.NotNull(journal);
        Assert.Equal(1, journal!.Format);
        Assert.Equal("renaming", journal.State);
        Assert.Single(journal.Plan);
        Assert.Equal("alpha.jpg", journal.Plan[0].Old);
        Assert.StartsWith("000001-", journal.Plan[0].New, StringComparison.Ordinal);
        Assert.Equal(30, journal.Plan[0].Mu);

        Assert.EndsWith(".rankmaster-rename.json", RenameEngine.JournalPath(dir), StringComparison.Ordinal);
    }

    [Fact]
    public void Deterministic_temps_are_identifiable()
    {
        Assert.Equal("__rm2_000001-a3f9.jpg", RenameEngine.TempNameOf("000001-a3f9.jpg"));

        var dir = Temp();
        var record = Rec("alpha.jpg", mu: 10, sigma: 5);
        File.WriteAllBytes(Path.Combine(dir, "alpha.jpg"), new byte[] { 1 });
        var plan = RenameEngine.BuildPlan(new List<MediaRecord> { record });
        var temp = RenameEngine.TempNameOf(plan[0].New);

        RenameEngine.MovePhase1(dir, plan);
        Assert.True(File.Exists(Path.Combine(dir, temp)),
            "phase 1 must move to the deterministic temp, not a random name — so a crash mid-run " +
            "leaves a file recovery can identify without a per-file journal write.");
        Assert.False(File.Exists(Path.Combine(dir, "alpha.jpg")));

        RenameEngine.MovePhase2(dir, plan);
        Assert.True(File.Exists(Path.Combine(dir, plan[0].New)));
        Assert.False(File.Exists(Path.Combine(dir, temp)));
    }

    [Fact]
    public void No_journal_present_is_a_silent_no_op_for_recovery_and_reunite()
    {
        var dir = Temp();
        var catalog = new JsonCatalog();
        File.WriteAllBytes(Path.Combine(dir, "alpha.jpg"), new byte[] { 1 });

        var recovered = RenameEngine.RecoverIfPresent(dir, catalog);
        Assert.False(recovered.JournalWasPresent);
        Assert.True(recovered.Reunited);

        var reunited = RenameEngine.ReuniteInPlace(dir, catalog);
        Assert.False(reunited.JournalWasPresent);
        Assert.True(reunited.Reunited);
    }

    /// <summary>
    /// A journal entry whose file has vanished entirely (not at old, temp or new) is the one honest
    /// rating loss the design accepts — the image itself no longer exists either (§ 3.3).
    /// </summary>
    [Fact]
    public void A_file_deleted_out_from_under_a_rename_drops_only_that_one_rating()
    {
        var dir = Temp();
        var catalog = new JsonCatalog();
        var records = new List<MediaRecord>
        {
            Rec("alpha.jpg", mu: 30, sigma: 1),
            Rec("bravo.jpg", mu: 20, sigma: 1),
        };
        foreach (var r in records)
            File.WriteAllBytes(Path.Combine(dir, r.Filename), new byte[] { 1 });

        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        // bravo vanishes before its move — someone deleted it out of band.
        var bravoEntry = plan.First(p => p.Old == "bravo.jpg");
        File.Delete(Path.Combine(dir, "bravo.jpg"));

        var outcome = RenameEngine.RecoverIfPresent(dir, catalog);
        Assert.True(outcome.Reunited);

        var loaded = catalog.Scan(dir);
        Assert.Single(loaded);   // only alpha survives; bravo's rating is gone with its file
        Assert.Equal(plan.First(p => p.Old == "alpha.jpg").New, loaded[0].Filename);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static MediaRecord Rec(
        string name, double mu, double sigma, int matches = 0, int impressions = 0, long lastPlayed = 0) =>
        new(new MediaId(name), MediaKind.Still, new Rating(mu, sigma), matches, impressions, lastPlayed);

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
