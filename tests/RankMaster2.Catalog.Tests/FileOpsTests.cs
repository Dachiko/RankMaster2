using RankMaster2.Catalog;
using RankMaster2.Ranking;
using Xunit;

namespace RankMaster2.Catalog.Tests;

public class FileOpsTests
{
    [Fact]
    public void UniqueFileName_AddsNumberWhenTaken()
    {
        var dir = Temp();
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "a (2).jpg"), [1]);
        Assert.Equal("a (3).jpg", FileOps.UniqueFileName(dir, "a.jpg"));
        Assert.Equal("b.jpg", FileOps.UniqueFileName(dir, "b.jpg"));
    }

    [Fact]
    public void MoveToSubfolder_CreatesSpecial1AndLeavesSourceGone()
    {
        var dir = Temp();
        File.WriteAllBytes(Path.Combine(dir, "shot.jpg"), [1, 2, 3]);
        var dest = FileOps.MoveToSubfolder(dir, "shot.jpg", FileOps.SpecialFolder);
        Assert.Equal("shot.jpg", dest);
        Assert.False(File.Exists(Path.Combine(dir, "shot.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, FileOps.SpecialFolder, "shot.jpg")));
    }

    [Fact]
    public void MoveToSubfolder_DoesNotOverwriteCollision()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, FileOps.DiscardedFolder));
        File.WriteAllBytes(Path.Combine(dir, FileOps.DiscardedFolder, "shot.jpg"), [9]);
        File.WriteAllBytes(Path.Combine(dir, "shot.jpg"), [1]);
        var dest = FileOps.MoveToSubfolder(dir, "shot.jpg", FileOps.DiscardedFolder);
        Assert.Equal("shot (2).jpg", dest);
        Assert.True(File.Exists(Path.Combine(dir, FileOps.DiscardedFolder, "shot.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, FileOps.DiscardedFolder, "shot (2).jpg")));
    }

    [Fact]
    public void RenameByConservativeScore_OrdersByMuMinusThreeSigma()
    {
        var dir = Temp();
        File.WriteAllBytes(Path.Combine(dir, "low.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "high.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "mid.jpg"), [1]);

        var records = new List<MediaRecord>
        {
            Rec("low.jpg", mu: 20, sigma: 2),   // 14
            Rec("high.jpg", mu: 30, sigma: 1),  // 27
            Rec("mid.jpg", mu: 26, sigma: 2),   // 20
        };

        var map = FileOps.RenameByConservativeScore(dir, records);
        Assert.Equal("000001.jpg", map[new MediaId("high.jpg")].Filename);
        Assert.Equal("000002.jpg", map[new MediaId("mid.jpg")].Filename);
        Assert.Equal("000003.jpg", map[new MediaId("low.jpg")].Filename);
        Assert.True(File.Exists(Path.Combine(dir, "000001.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "high.jpg")));
    }

    [Fact]
    public void BackupAndRestore_PutsFilesBack()
    {
        var dir = Temp();
        File.WriteAllText(Path.Combine(dir, "keep.jpg"), "orig");
        var backup = FileOps.BackupLibrary(dir, new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        Assert.True(Directory.Exists(backup));
        File.WriteAllText(Path.Combine(dir, "keep.jpg"), "changed");
        File.WriteAllText(Path.Combine(dir, "extra.jpg"), "new");
        FileOps.RestoreLibrary(dir, backup);
        Assert.Equal("orig", File.ReadAllText(Path.Combine(dir, "keep.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "extra.jpg")));
    }

    [Fact]
    public void RemapAfterRename_KeepsRatings()
    {
        var catalog = new JsonCatalog();
        var rec = Rec("old.jpg", mu: 28, sigma: 3);
        rec = rec with { Matches = 4 };
        var mapped = catalog.RemapIds(
            [rec],
            new Dictionary<MediaId, MediaId> { [rec.Id] = new MediaId("000001.jpg") });
        Assert.Equal("000001.jpg", mapped[0].Filename);
        Assert.Equal(28, mapped[0].Rating.Mu);
        Assert.Equal(4, mapped[0].Matches);
    }

    private static MediaRecord Rec(string name, double mu, double sigma) =>
        new(new MediaId(name), MediaKind.Still, new Rating(mu, sigma), 0, 0, 0);

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
