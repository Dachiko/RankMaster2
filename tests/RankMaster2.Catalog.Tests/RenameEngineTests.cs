using System.Diagnostics;
using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Catalog.Tests;

/// <summary>
/// The acceptance gate for SERVER_SPEC.md § 10.16: a crash at any step of the rename resolves, after
/// recovery, to "every rating still finds its file." Every interruption is driven deterministically —
/// build the plan, run exactly as far as the step names, stop, and inspect the folder as a fresh
/// process would find it — rather than racing a real process kill, which covers every step instead of
/// one lucky moment.
/// </summary>
public class RenameEngineTests
{
    /// <summary>Every name one run produces (SERVER_SPEC.md § 10.16; G-audit-remediation § 1.1).</summary>
    private const string NamePattern = @"^\d{6}-[0-9a-f]{4}\.[^.]+$";

    // ---- the crash matrix (SERVER_SPEC.md § 10.16) ---------------------------------------------

    public static IEnumerable<object[]> InterruptionPoints() => new[]
    {
        new object[] { "after journal fsync, before any move" },
        new object[] { "after k of N moves" },
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

        var plan = RenameEngine.BuildPlan(dir, records);
        Assert.Equal("alpha.jpg", plan[0].Old);              // highest conservative score
        Assert.Matches(NamePattern, plan[0].New);
        Assert.StartsWith("000001-", plan[0].New, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", plan[0].New, StringComparison.Ordinal);

        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        Assert.True(RenameEngine.JournalExists(dir), "the journal must exist before any move (§ 10.16)");

        switch (step)
        {
            case "after journal fsync, before any move":
                break;   // stop here: nothing moved yet

            case "after k of N moves":
                RenameEngine.MoveAll(dir, plan, shouldStop: done => done >= 1);
                break;

            case "after all moves, before the DB save":
                RenameEngine.MoveAll(dir, plan);
                // The dangerous moment: every file has its new name and nothing has told the
                // database yet. This folder has no rankmaster_db.json at all, which is the harshest
                // version of that danger — recovery must still work with no on-disk DB to lean on;
                // the dedicated test below covers the case where a stale DB is actively misleading.
                break;

            case "after the DB save, before the journal delete":
                RenameEngine.MoveAll(dir, plan);
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
    /// The dangerous moment is files renamed, database not — because the *next ordinary save* through
    /// the plain `Scan`/merge path would silently drop every rating. This constructs that exact state
    /// by hand (a real `rankmaster_db.json` with the *old* keys, files already at their *new* names)
    /// and proves both halves: the plain merge loses the ratings, and `RecoverIfPresent` followed by
    /// `Scan` does not.
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

        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        RenameEngine.MoveAll(dir, plan);

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

        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        // Stop mid-renaming: one file moved, the rest untouched.
        RenameEngine.MoveAll(dir, plan, shouldStop: done => done >= 1);

        // A genuinely mixed folder: some original names, some new.
        var onDiskNames = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name != RenameEngine.JournalFileName)
            .ToHashSet();
        Assert.Contains(plan[0].New, onDiskNames);          // moved
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

    /// <summary>
    /// The owner's reason for the Cancel button is a slow USB drive: he wants to stop the run the
    /// moment it looks wrong (SERVER_SPEC.md § 10.16, "Why there is a bar and a cancel"). A move
    /// waiting on a file another program holds must therefore notice the cancel <b>between its
    /// retries</b> — within one retry interval, not after the whole retry budget — and nothing after
    /// the cancelled file may move.
    /// </summary>
    [Fact]
    public void Cancel_lands_between_retries_of_a_locked_move()
    {
        var dir = Temp();
        var records = new List<MediaRecord>
        {
            Rec("alpha.jpg", mu: 30, sigma: 1),
            Rec("bravo.jpg", mu: 20, sigma: 1),
        };
        foreach (var r in records)
            File.WriteAllBytes(Path.Combine(dir, r.Filename), new byte[] { 1 });

        var plan = RenameEngine.BuildPlan(dir, records);

        // alpha's destination already exists and is held open by "another program", so the move
        // cannot complete and is retried. (The engine never overwrites: a destination that exists is
        // an IOException, § 10.16 "One move per file".)
        var blocked = Path.Combine(dir, plan[0].New);
        File.WriteAllBytes(blocked, new byte[] { 9 });
        using var hold = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Cancel arrives after the first retry: the first poll (before the move) says keep going.
        var polls = 0;
        var clock = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() =>
            RenameEngine.MoveAll(dir, plan, shouldStop: _ => polls++ >= 2));
        clock.Stop();

        // The full budget is twenty retries of fifty milliseconds. Landing inside half a second is
        // "one retry interval", not "the whole budget".
        Assert.True(clock.ElapsedMilliseconds < 500,
            $"cancel took {clock.ElapsedMilliseconds} ms; it must land within one retry interval, " +
            "not after the whole retry budget.");

        // Nothing after the cancelled file moved, and the cancelled file is still where it was.
        Assert.True(File.Exists(Path.Combine(dir, "alpha.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, "bravo.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, plan[1].New)));
    }

    // ---- the run suffix ---------------------------------------------------------------------

    /// <summary>
    /// § 1.1: a candidate suffix is rejected while <b>any</b> name in play — a record's, or a file in
    /// the folder that no record mentions — ends in <c>-ssss</c> with its extension removed, whatever
    /// its rank or stem. Stronger than "no new name collides", and the reason the old and new name
    /// sets of a run can never overlap.
    /// </summary>
    [Fact]
    public void Run_suffix_is_never_one_already_on_disk()
    {
        // A file of a previous run sits in the folder, and the session's records do not mention it.
        var dir = Temp();
        var records = new List<MediaRecord> { Rec("alpha.jpg", mu: 30, sigma: 1) };
        File.WriteAllBytes(Path.Combine(dir, "alpha.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "000001-7f3a.jpg"), new byte[] { 2 });

        var plan = RenameEngine.BuildPlan(dir, records, drawSuffix: Draws("7f3a", "b91c"));
        Assert.Equal("000001-b91c.jpg", plan[0].New);

        // The same rule on a name of any shape and any rank: -7f3a at the end of the stem is enough.
        var other = Temp();
        var camera = new List<MediaRecord> { Rec("IMG_9-7f3a.jpg", mu: 30, sigma: 1) };
        File.WriteAllBytes(Path.Combine(other, "IMG_9-7f3a.jpg"), new byte[] { 1 });

        var second = RenameEngine.BuildPlan(other, camera, drawSuffix: Draws("7f3a", "b91c"));
        Assert.Equal("000001-b91c.jpg", second[0].New);

        // …and the records-only overload applies it to the names it is given.
        var third = RenameEngine.BuildPlan(camera, drawSuffix: Draws("7f3a", "b91c"));
        Assert.Equal("000001-b91c.jpg", third[0].New);
    }

    /// <summary>
    /// § 1.1: sixteen rejections, then the run fails — never a wider suffix, which would leave one
    /// library holding names of two shapes. Nothing has moved when it fails, so nothing is at risk.
    /// </summary>
    [Fact]
    public void Sixteen_rejected_draws_fail_before_anything_moves()
    {
        var dir = Temp();
        var records = new List<MediaRecord> { Rec("alpha.jpg", mu: 30, sigma: 1) };
        File.WriteAllBytes(Path.Combine(dir, "alpha.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "000001-7f3a.jpg"), new byte[] { 2 });

        var draws = 0;
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            RenameEngine.BuildPlan(dir, records, drawSuffix: () => { draws++; return "7f3a"; }));

        Assert.Equal(16, draws);
        Assert.Contains("Nothing has been moved", thrown.Message, StringComparison.Ordinal);

        // The folder is exactly as it was: no journal, no move, no database.
        Assert.False(RenameEngine.JournalExists(dir));
        Assert.Equal(
            new[] { "000001-7f3a.jpg", "alpha.jpg" },
            Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The whole point of renaming: sorting the folder by name sorts it by rank. Fixed-width index
    /// first, one suffix for the entire run.
    /// </summary>
    [Fact]
    public void Names_of_one_run_sort_in_rank_order()
    {
        var records = new List<MediaRecord>
        {
            Rec("zulu.jpg", mu: 20, sigma: 1),      // third
            Rec("alpha.png", mu: 40, sigma: 1),     // first
            Rec("mike.jpg", mu: 30, sigma: 1),      // second
        };

        var plan = RenameEngine.BuildPlan(records);

        Assert.Equal(new[] { "alpha.png", "mike.jpg", "zulu.jpg" }, plan.Select(e => e.Old));
        Assert.All(plan, e => Assert.Matches(NamePattern, e.New));
        Assert.Equal(
            plan.Select(e => e.New),
            plan.Select(e => e.New).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Single(plan.Select(e => Path.GetFileNameWithoutExtension(e.New).Split('-')[1]).Distinct());
        Assert.EndsWith(".png", plan[0].New, StringComparison.Ordinal);   // the extension is the file's own
    }

    /// <summary>
    /// The property recovery rests on, stated directly: no new name of a run is any entry's old name.
    /// A plan that violates it is a bug in the engine, and `BuildPlan` refuses to return one — which
    /// is what a forced suffix that reproduces the folder's own names proves.
    /// </summary>
    [Fact]
    public void Old_and_new_names_are_disjoint()
    {
        var dir = Temp();
        var records = new List<MediaRecord>
        {
            Rec("000001-7f3a.jpg", mu: 20, sigma: 1),   // more voting has changed the order
            Rec("000002-7f3a.jpg", mu: 30, sigma: 1),
            Rec("000003-7f3a.jpg", mu: 25, sigma: 1),
        };
        foreach (var r in records)
            File.WriteAllBytes(Path.Combine(dir, r.Filename), new byte[] { 1 });

        var plan = RenameEngine.BuildPlan(dir, records);
        var olds = plan.Select(e => e.Old).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(plan, e => Assert.DoesNotContain(e.New, olds));

        // Forcing last run's suffix would produce exactly the folder's own names — a plan in which
        // one entry's new name is another's old one. That is the corruption H1 was, and it is now
        // refused rather than executed.
        Assert.Throws<InvalidOperationException>(() => RenameEngine.BuildPlan(records, runSuffix: "7f3a"));
    }

    // ---- the journal itself -----------------------------------------------------------------

    [Fact]
    public void The_journal_is_ignored_by_scan_and_carries_the_ratings()
    {
        var dir = Temp();
        var catalog = new JsonCatalog();
        var records = new List<MediaRecord> { Rec("alpha.jpg", mu: 30, sigma: 1) };
        File.WriteAllBytes(Path.Combine(dir, "alpha.jpg"), new byte[] { 1 });

        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        // Scan must never list the journal file as a media record.
        var scanned = catalog.Scan(dir);
        Assert.Single(scanned);
        Assert.Equal("alpha.jpg", scanned[0].Filename);

        var journal = RenameEngine.ReadJournal(dir);
        Assert.NotNull(journal);
        Assert.Equal(2, journal!.Format);
        Assert.Equal("renaming", journal.State);
        Assert.Equal(Path.GetFileNameWithoutExtension(plan[0].New).Split('-')[1], journal.Suffix);
        Assert.Matches("^[0-9a-f]{4}$", journal.Suffix!);
        Assert.Single(journal.Plan);
        Assert.Equal("alpha.jpg", journal.Plan[0].Old);
        Assert.Matches(NamePattern, journal.Plan[0].New);
        Assert.Equal(30, journal.Plan[0].Mu);

        Assert.EndsWith(".rankmaster-rename.json", RenameEngine.JournalPath(dir), StringComparison.Ordinal);
    }

    /// <summary>
    /// A format-1 journal (no `suffix`) still reads: they exist in rm2ctl scratch folders, and the
    /// suffix field is a convenience — the names in the plan already carry it.
    /// </summary>
    [Fact]
    public void A_format_1_journal_still_reads()
    {
        var dir = Temp();
        File.WriteAllBytes(Path.Combine(dir, "000001-7f3a.jpg"), new byte[] { 1 });
        File.WriteAllText(RenameEngine.JournalPath(dir), """
            { "format": 1, "state": "renaming", "createdAt": "2026-09-17T18:04:11.400Z",
              "plan": [ { "old": "alpha.jpg", "new": "000001-7f3a.jpg",
                          "mu": 30, "sigma": 1, "matches": 9, "impressions": 9, "lastPlayed": 111 } ] }
            """);

        var journal = RenameEngine.ReadJournal(dir);
        Assert.NotNull(journal);
        Assert.Equal(1, journal!.Format);
        Assert.Null(journal.Suffix);

        var outcome = RenameEngine.RecoverIfPresent(dir, new JsonCatalog());
        Assert.True(outcome.Reunited);
        Assert.Equal(30, new JsonCatalog().Scan(dir).Single().Rating.Mu);
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.16: a journal that cannot be trusted is refused, never guessed at. Each
    /// row is a file that says something the engine cannot have written; recovery must throw
    /// `InvalidDataException` (which the server turns into `rename_failed`, releasing the lock) and
    /// must not have touched a single file or written a database on the way.
    /// </summary>
    [Theory]
    [InlineData("truncated", "{ \"format\": 2, \"state\": \"renaming\", \"plan\": [ { \"old\": \"alph")]
    [InlineData("a null plan", "{ \"format\": 2, \"state\": \"renaming\", \"createdAt\": \"z\", \"plan\": null }")]
    [InlineData("a format from the future", """
        { "format": 3, "state": "renaming", "createdAt": "z", "suffix": "7f3a",
          "plan": [ { "old": "alpha.jpg", "new": "000001-7f3a.jpg", "mu": 30, "sigma": 1,
                      "matches": 0, "impressions": 0, "lastPlayed": 0 } ] }
        """)]
    [InlineData("intersecting old and new sets", """
        { "format": 2, "state": "renaming", "createdAt": "z", "suffix": "7f3a",
          "plan": [ { "old": "000001.jpg", "new": "000002.jpg", "mu": 30, "sigma": 1,
                      "matches": 0, "impressions": 0, "lastPlayed": 0 },
                    { "old": "000002.jpg", "new": "000001.jpg", "mu": 20, "sigma": 1,
                      "matches": 0, "impressions": 0, "lastPlayed": 0 } ] }
        """)]
    public void An_unreadable_journal_is_refused_not_guessed_at(string what, string journalText)
    {
        var dir = Temp();
        File.WriteAllBytes(Path.Combine(dir, "000001.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "000002.jpg"), new byte[] { 2 });
        File.WriteAllText(RenameEngine.JournalPath(dir), journalText);

        Assert.Throws<InvalidDataException>(() => RenameEngine.ReadJournal(dir));
        Assert.Throws<InvalidDataException>(() => RenameEngine.RecoverIfPresent(dir, new JsonCatalog()));
        Assert.Throws<InvalidDataException>(() => RenameEngine.ReuniteInPlace(dir, new JsonCatalog()));

        Assert.True(RenameEngine.JournalExists(dir), $"[{what}] the journal is left for the owner to look at.");
        Assert.False(File.Exists(Path.Combine(dir, JsonCatalog.FileName)),
            $"[{what}] a journal that cannot be read must not produce a database.");
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(dir, "000001.jpg")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(dir, "000002.jpg")));
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
    /// A journal entry whose file has vanished entirely (neither at old nor at new) is the one honest
    /// rating loss the design accepts — the image itself no longer exists either (§ 10.16).
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

        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        // bravo vanishes before its move — someone deleted it out of band.
        File.Delete(Path.Combine(dir, "bravo.jpg"));

        var outcome = RenameEngine.RecoverIfPresent(dir, catalog);
        Assert.True(outcome.Reunited);

        var loaded = catalog.Scan(dir);
        Assert.Single(loaded);   // only alpha survives; bravo's rating is gone with its file
        Assert.Equal(plan.First(p => p.Old == "alpha.jpg").New, loaded[0].Filename);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>A forced sequence of suffix draws, so a collision can be arranged rather than waited for.</summary>
    private static Func<string> Draws(params string[] suffixes)
    {
        var next = 0;
        return () => suffixes[Math.Min(next++, suffixes.Length - 1)];
    }

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
