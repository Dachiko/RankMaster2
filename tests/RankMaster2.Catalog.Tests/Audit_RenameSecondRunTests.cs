using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Catalog.Tests;

/// <summary>
/// AUDIT THROWAWAY. The second rename of a folder that was already renamed once: every file is
/// already called 000001.jpg…, and after more voting the order has changed, so the plan maps
/// names that are simultaneously an `old` of one entry and a `new` of another. Recovery / cancel
/// look files up by new → temp → old; when the run stopped during phase 1, an unmoved `old` file
/// whose name is also somebody else's `new` is misidentified.
/// </summary>
public class Audit_RenameSecondRunTests
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
    public void Plan_of_a_second_rename_reuses_names()
    {
        var (dir, records) = Arrange();
        var plan = RenameEngine.BuildPlan(records);
        // C (mu 30) -> 000001, A (mu 25) -> 000002, B (mu 20) -> 000003
        Assert.Equal(("000003.jpg", "000001.jpg"), (plan[0].Old, plan[0].New));
        Assert.Equal(("000001.jpg", "000002.jpg"), (plan[1].Old, plan[1].New));
        Assert.Equal(("000002.jpg", "000003.jpg"), (plan[2].Old, plan[2].New));
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
        // The owner presses cancel after the first move: 000003.jpg -> __rm2_000001.jpg.
        RenameEngine.MovePhase1(dir, plan, shouldStop: done => done >= 1);

        var outcome = RenameEngine.ReuniteInPlace(dir, new JsonCatalog());
        Assert.True(outcome.Reunited);

        var by = RatingByBytes(dir);
        Assert.Equal(30, by[0xC].Mu);   // C, now at __rm2_000001.jpg
        Assert.Equal(25, by[0xA].Mu);   // A, still at 000001.jpg
        Assert.Equal(20, by[0xB].Mu);   // B, still at 000002.jpg
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
