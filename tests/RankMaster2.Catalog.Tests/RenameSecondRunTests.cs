using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Catalog.Tests;

/// <summary>
/// The second rename of a folder that was already renamed once — the case that corrupted ratings
/// before the per-run suffix existed, and the reason the suffix exists.
///
/// <para>Every file here is already called 000001.jpg…, which is what Rank Master 2's own rename
/// produces, so this is the owner's ordinary folder and not a contrived one. More voting has
/// changed the order, so without a suffix the plan would map names that are simultaneously one
/// entry's <c>old</c> and another's <c>new</c>; recovery and cancel, which identify a file by asking
/// whether its name is a <c>new</c> name or an <c>old</c> one, would then hand a rating to the wrong
/// picture whenever the run stopped part-way through the moves.</para>
///
/// <para>Each file's bytes are its identity, so every test here checks the thing that actually
/// matters — that each rating came back to the same picture — rather than checking names.</para>
/// </summary>
public class RenameSecondRunTests
{
    // Each file's bytes are its identity, so we can tell which rating landed on which picture.
    private static readonly byte[] BytesA = { 0xA };   // was 000001.jpg, now ranked 2nd
    private static readonly byte[] BytesB = { 0xB };   // was 000002.jpg, now ranked 3rd
    private static readonly byte[] BytesC = { 0xC };   // was 000003.jpg, now ranked 1st

    private static (string dir, List<MediaRecord> records) Arrange(string suffix = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-second-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string Name(int index) => suffix.Length == 0 ? $"{index:D6}.jpg" : $"{index:D6}-{suffix}.jpg";

        File.WriteAllBytes(Path.Combine(dir, Name(1)), BytesA);
        File.WriteAllBytes(Path.Combine(dir, Name(2)), BytesB);
        File.WriteAllBytes(Path.Combine(dir, Name(3)), BytesC);

        var records = new List<MediaRecord>
        {
            new(new MediaId(Name(1)), MediaKind.Still, new Rating(25, 1), 10, 10, 1),
            new(new MediaId(Name(2)), MediaKind.Still, new Rating(20, 1), 20, 20, 2),
            new(new MediaId(Name(3)), MediaKind.Still, new Rating(30, 1), 30, 30, 3),
        };
        var catalog = new JsonCatalog();
        catalog.Save(dir, records);
        return (dir, records);
    }

    private static Dictionary<byte, Rating> RatingByBytes(string dir)
    {
        var scanned = new JsonCatalog().Scan(dir);
        var result = new Dictionary<byte, Rating>();
        foreach (var r in scanned)
        {
            var b = File.ReadAllBytes(Path.Combine(dir, r.Filename))[0];
            result[b] = r.Rating;
        }
        return result;
    }

    [Fact]
    public void Plan_of_a_second_rename_reuses_no_name_already_in_the_folder()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(dir, records);

        // The ranking is unchanged: C (mu 30) first, A (mu 25) second, B (mu 20) third.
        Assert.Equal(new[] { "000003.jpg", "000001.jpg", "000002.jpg" }, plan.Select(e => e.Old));

        // The property the whole recovery design rests on: no new name is a name the folder already
        // carries. Without it, new and old overlap and a file on disk stops identifying one picture.
        var onDisk = new HashSet<string>(
            Directory.GetFiles(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan)
            Assert.DoesNotContain(entry.New, onDisk);

        // Still sorted by rank, which is the reason for renaming at all.
        Assert.Equal(plan.Select(e => e.New), plan.Select(e => e.New).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Two_runs_over_the_same_folder_do_not_produce_the_same_names()
    {
        var (dir, records) = Arrange();

        var first = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, first, DateTimeOffset.UtcNow);
        RenameEngine.MoveAll(dir, first);
        var renamed = RenameEngine.Commit(new JsonCatalog(), dir, records, first);
        RenameEngine.DeleteJournal(dir);

        // The folder now holds the first run's names, so the second run cannot draw that suffix —
        // this is a verified property, not a coincidence of two random draws.
        var second = RenameEngine.BuildPlan(dir, renamed);

        Assert.NotEqual(first[0].New, second[0].New);
        foreach (var entry in second)
            Assert.DoesNotContain(entry.New, first.Select(e => e.New));
    }

    [Fact]
    public void Happy_path_second_rename_keeps_every_rating_with_its_bytes()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        RenameEngine.MoveAll(dir, plan);
        RenameEngine.Commit(new JsonCatalog(), dir, records, plan);
        RenameEngine.DeleteJournal(dir);

        var by = RatingByBytes(dir);
        Assert.Equal(30, by[0xC].Mu);
        Assert.Equal(25, by[0xA].Mu);
        Assert.Equal(20, by[0xB].Mu);
    }

    [Fact]
    public void Cancel_part_way_through_a_second_rename_keeps_every_rating_with_its_bytes()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        // The owner presses cancel after the first move: 000003.jpg -> its new name.
        RenameEngine.MoveAll(dir, plan, shouldStop: done => done >= 1);

        var outcome = RenameEngine.ReuniteInPlace(dir, new JsonCatalog());
        Assert.True(outcome.Reunited);

        var by = RatingByBytes(dir);
        Assert.Equal(30, by[0xC].Mu);   // C, at its new name
        Assert.Equal(25, by[0xA].Mu);   // A, never moved
        Assert.Equal(20, by[0xB].Mu);   // B, never moved
    }

    [Fact]
    public void Crash_part_way_through_a_second_rename_recovers_every_rating_with_its_bytes()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        RenameEngine.MoveAll(dir, plan, shouldStop: done => done >= 1);

        var outcome = RenameEngine.RecoverIfPresent(dir, new JsonCatalog());
        Assert.True(outcome.Reunited);
        Assert.False(RenameEngine.JournalExists(dir));

        var by = RatingByBytes(dir);
        Assert.Equal(3, by.Count);
        Assert.Equal(30, by[0xC].Mu);
        Assert.Equal(25, by[0xA].Mu);
        Assert.Equal(20, by[0xB].Mu);
    }

    [Fact]
    public void Crash_after_journal_before_any_move_of_a_second_rename_recovers_every_rating()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        // Nothing moved at all. The spec's table says: "files are still old; reunite writes the same DB; harmless".

        var outcome = RenameEngine.RecoverIfPresent(dir, new JsonCatalog());
        Assert.True(outcome.Reunited);

        var by = RatingByBytes(dir);
        Assert.Equal(3, by.Count);
        Assert.Equal(30, by[0xC].Mu);
        Assert.Equal(25, by[0xA].Mu);
        Assert.Equal(20, by[0xB].Mu);
    }

    // ---- T1d: a folder this server renamed before, interrupted at every step ---------------------

    public static IEnumerable<object[]> InterruptionPoints() => new[]
    {
        new object[] { "before any move" },
        new object[] { "after k of N moves" },
        new object[] { "after all moves" },
        new object[] { "after the DB save" },
    };

    /// <summary>
    /// § 2 T1d, the hole the audit found in the evidence: the engine's tests only ever renamed folders
    /// of fresh camera names. This is the folder the owner actually has on the *third* rename — one
    /// this server itself named <c>000001-b91c.jpg …</c> — stopped at every point a crash or a cancel
    /// can stop it. Whatever the interruption, the new run's suffix differs from the old one and every
    /// rating is still on the same bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(InterruptionPoints))]
    public void A_folder_this_server_already_renamed_survives_an_interruption_at_every_step(string step)
    {
        var (dir, records) = Arrange(suffix: "b91c");
        var catalog = new JsonCatalog();

        var plan = RenameEngine.BuildPlan(dir, records);

        // A fresh suffix, never the one the folder already carries — which is what makes every name
        // below belong to exactly one run.
        var suffix = Path.GetFileNameWithoutExtension(plan[0].New).Split('-')[1];
        Assert.NotEqual("b91c", suffix);
        Assert.All(plan, e => Assert.Matches(@"^\d{6}-[0-9a-f]{4}\.[^.]+$", e.New));
        Assert.All(plan, e => Assert.DoesNotContain(e.New, plan.Select(p => p.Old)));

        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        switch (step)
        {
            case "before any move":
                break;
            case "after k of N moves":
                RenameEngine.MoveAll(dir, plan, shouldStop: done => done >= 2);
                break;
            case "after all moves":
                RenameEngine.MoveAll(dir, plan);
                break;
            case "after the DB save":
                RenameEngine.MoveAll(dir, plan);
                RenameEngine.Commit(catalog, dir, records, plan);
                break;
            default:
                throw new InvalidOperationException(step);
        }

        var outcome = RenameEngine.RecoverIfPresent(dir, catalog);
        Assert.True(outcome.Reunited, $"[{step}] recovery must reunite every rating.");
        Assert.False(RenameEngine.JournalExists(dir));

        var by = RatingByBytes(dir);
        Assert.Equal(3, by.Count);
        Assert.Equal(30, by[0xC].Mu);
        Assert.Equal(25, by[0xA].Mu);
        Assert.Equal(20, by[0xB].Mu);
    }

    /// <summary>The same folder, cancelled instead of crashed: reunite in place, no rating moved.</summary>
    [Theory]
    [MemberData(nameof(InterruptionPoints))]
    public void A_folder_this_server_already_renamed_survives_a_cancel_at_every_step(string step)
    {
        var (dir, records) = Arrange(suffix: "b91c");
        var catalog = new JsonCatalog();
        var plan = RenameEngine.BuildPlan(dir, records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);

        switch (step)
        {
            case "before any move":
                break;
            case "after k of N moves":
                RenameEngine.MoveAll(dir, plan, shouldStop: done => done >= 2);
                break;
            case "after all moves":
                RenameEngine.MoveAll(dir, plan);
                break;
            case "after the DB save":
                RenameEngine.MoveAll(dir, plan);
                RenameEngine.Commit(catalog, dir, records, plan);
                break;
            default:
                throw new InvalidOperationException(step);
        }

        var before = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name != RenameEngine.JournalFileName && name != JsonCatalog.FileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var outcome = RenameEngine.ReuniteInPlace(dir, catalog);
        Assert.True(outcome.Reunited, $"[{step}] cancel must reunite every rating.");

        // Cancel stops where it is: it finishes no outstanding move.
        var after = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name != JsonCatalog.FileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(before, after);

        var by = RatingByBytes(dir);
        Assert.Equal(3, by.Count);
        Assert.Equal(30, by[0xC].Mu);
        Assert.Equal(25, by[0xA].Mu);
        Assert.Equal(20, by[0xB].Mu);
    }
}
