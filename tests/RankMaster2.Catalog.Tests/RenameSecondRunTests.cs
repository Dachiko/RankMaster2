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
/// entry's <c>old</c> and another's <c>new</c>; recovery and cancel, which identify a file by
/// asking whether its name is a <c>new</c>, a temp or an <c>old</c>, would then hand a rating to
/// the wrong picture whenever the run stopped mid-phase-1.</para>
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

    private static (string dir, List<MediaRecord> records) Arrange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "000001.jpg"), BytesA);
        File.WriteAllBytes(Path.Combine(dir, "000002.jpg"), BytesB);
        File.WriteAllBytes(Path.Combine(dir, "000003.jpg"), BytesC);

        var records = new List<MediaRecord>
        {
            new(new MediaId("000001.jpg"), MediaKind.Still, new Rating(25, 1), 10, 10, 1),
            new(new MediaId("000002.jpg"), MediaKind.Still, new Rating(20, 1), 20, 20, 2),
            new(new MediaId("000003.jpg"), MediaKind.Still, new Rating(30, 1), 30, 30, 3),
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
        var plan = RenameEngine.BuildPlan(records);

        // The ranking is unchanged: C (mu 30) first, A (mu 25) second, B (mu 20) third.
        Assert.Equal(new[] { "000003.jpg", "000001.jpg", "000002.jpg" }, plan.Select(e => e.Old));

        // The property the whole recovery design rests on: no new name, and no temporary, is a name
        // the folder already carries. Without it, new/temp/old overlap and a file on disk stops
        // identifying one picture.
        var onDisk = new HashSet<string>(
            Directory.GetFiles(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan)
        {
            Assert.DoesNotContain(entry.New, onDisk);
            Assert.DoesNotContain(RenameEngine.TempNameOf(entry.New), onDisk);
        }

        // Still sorted by rank, which is the reason for renaming at all.
        Assert.Equal(plan.Select(e => e.New), plan.Select(e => e.New).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Two_runs_over_the_same_folder_do_not_produce_the_same_names()
    {
        var (dir, records) = Arrange();
        var first = RenameEngine.BuildPlan(records);
        var second = RenameEngine.BuildPlan(records);

        Assert.NotEqual(first[0].New, second[0].New);
    }

    [Fact]
    public void Happy_path_second_rename_keeps_every_rating_with_its_bytes()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        RenameEngine.MovePhase1(dir, plan);
        RenameEngine.MovePhase2(dir, plan);
        RenameEngine.Commit(new JsonCatalog(), dir, records, plan);
        RenameEngine.DeleteJournal(dir);

        var by = RatingByBytes(dir);
        Assert.Equal(30, by[0xC].Mu);
        Assert.Equal(25, by[0xA].Mu);
        Assert.Equal(20, by[0xB].Mu);
    }

    [Fact]
    public void Cancel_during_phase1_of_a_second_rename_keeps_every_rating_with_its_bytes()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        // The owner presses cancel after the first move: 000003.jpg -> its temporary.
        RenameEngine.MovePhase1(dir, plan, shouldStop: done => done >= 1);

        var outcome = RenameEngine.ReuniteInPlace(dir, new JsonCatalog());
        Assert.True(outcome.Reunited);

        var by = RatingByBytes(dir);
        Assert.Equal(30, by[0xC].Mu);   // C, now at its temporary
        Assert.Equal(25, by[0xA].Mu);   // A, never moved
        Assert.Equal(20, by[0xB].Mu);   // B, never moved
    }

    [Fact]
    public void Crash_during_phase1_of_a_second_rename_recovers_every_rating_with_its_bytes()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(records);
        RenameEngine.WriteJournal(dir, plan, DateTimeOffset.UtcNow);
        RenameEngine.MovePhase1(dir, plan, shouldStop: done => done >= 1);

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
        var plan = RenameEngine.BuildPlan(records);
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
}
