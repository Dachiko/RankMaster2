using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

public class LastFolderStoreTests
{
    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("rm2-ui-lastfolder-").FullName, "last-folder.txt");

    [Fact]
    public void Nothing_stored_yet_loads_null()
    {
        var store = new LastFolderStore(TempFile());
        Assert.Null(store.Load());
    }

    [Fact]
    public void A_saved_existing_directory_loads_back()
    {
        var store = new LastFolderStore(TempFile());
        var folder = Directory.CreateTempSubdirectory("rm2-ui-lastfolder-target-").FullName;
        store.Save(folder);
        Assert.Equal(folder, store.Load());
    }

    [Fact]
    public void A_stored_path_to_a_missing_directory_loads_null()
    {
        var store = new LastFolderStore(TempFile());
        var folder = Path.Combine(Path.GetTempPath(), "rm2-does-not-exist-" + Guid.NewGuid().ToString("N"));
        store.Save(folder);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_creates_the_containing_directory_if_needed()
    {
        // A directory nested one level below a fresh temp dir, not created here -- Save must
        // create it itself.
        var path = Path.Combine(Directory.CreateTempSubdirectory("rm2-ui-lastfolder-").FullName, "nested", "last-folder.txt");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        var store = new LastFolderStore(path);
        var folder = Directory.CreateTempSubdirectory("rm2-ui-lastfolder-target2-").FullName;
        store.Save(folder);
        Assert.True(File.Exists(path));
        Assert.Equal(folder, store.Load());
    }
}
