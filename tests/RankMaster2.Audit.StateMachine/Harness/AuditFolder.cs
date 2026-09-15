using RankMaster2.Server.Tests.Fixtures;

namespace RankMaster2.Audit.StateMachine.Harness;

/// <summary>
/// A throwaway library folder that the test can break on purpose, mid-session.
///
/// The audit needs three things <see cref="LibraryFolder"/> does not offer: a way to make
/// <c>JsonCatalog.Save</c> fail without making the folder unwritable (a discard has to be able to
/// move a file while the save still throws — that is the whole rollback-asymmetry case of
/// SERVER_SPEC.md § 8.3), a way to make a single move fail, and real media from
/// <c>tests/corpus</c> for the files-changed-underneath cases.
/// </summary>
public sealed class AuditFolder : IDisposable
{
    public const string DatabaseName = "rankmaster_db.json";
    public const string DatabaseTemp = DatabaseName + ".tmp";

    public string Path { get; }

    private AuditFolder(string path) => Path = path;

    public static AuditFolder Create(string label)
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rm2-audit", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new AuditFolder(root);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);
    public bool Has(string name) => System.IO.File.Exists(File(name));
    public byte[] Bytes(string name) => System.IO.File.ReadAllBytes(File(name));
    public string DatabasePath => File(DatabaseName);

    public string Discarded(string name) => System.IO.Path.Combine(Path, "discarded", name);
    public string Special(string name) => System.IO.Path.Combine(Path, "special 1", name);

    public AuditFolder Write(string name, byte[] bytes)
    {
        System.IO.File.WriteAllBytes(File(name), bytes);
        return this;
    }

    public AuditFolder WriteText(string name, string text)
    {
        System.IO.File.WriteAllText(File(name), text);
        return this;
    }

    public AuditFolder Delete(string name)
    {
        System.IO.File.Delete(File(name));
        return this;
    }

    public AuditFolder Rename(string from, string to)
    {
        System.IO.File.Move(File(from), File(to));
        return this;
    }

    // ---- deliberate breakage ------------------------------------------------------------------

    /// <summary>
    /// Makes every <c>JsonCatalog.Save</c> in this folder throw, while leaving the folder itself
    /// writable so <c>FileOps.MoveToSubfolder</c> still succeeds. The catalog writes
    /// <c>rankmaster_db.json.tmp</c> first, so a directory sitting on that name fails the write and
    /// nothing else. That is the only way to reach the "the file moved but the save threw" row of
    /// SERVER_SPEC.md § 8.3.
    /// </summary>
    public AuditFolder JamSave()
    {
        Directory.CreateDirectory(File(DatabaseTemp));
        return this;
    }

    public AuditFolder UnjamSave()
    {
        var jam = File(DatabaseTemp);
        if (Directory.Exists(jam)) Directory.Delete(jam, recursive: true);
        return this;
    }

    public bool SaveIsJammed => Directory.Exists(File(DatabaseTemp));

    /// <summary>
    /// Makes a discard's move fail: <c>FileOps.MoveToSubfolder</c> calls
    /// <c>Directory.CreateDirectory("discarded")</c>, which throws when a plain file already holds
    /// that name. Nothing has moved when it does, which is exactly the § 8.3 "move throws" row.
    /// </summary>
    public AuditFolder JamDiscardFolder()
    {
        System.IO.File.WriteAllText(File("discarded"), "not a directory");
        return this;
    }

    public AuditFolder UnjamDiscardFolder()
    {
        var jam = File("discarded");
        if (System.IO.File.Exists(jam)) System.IO.File.Delete(jam);
        return this;
    }

    // ---- profiles ------------------------------------------------------------------------------

    public static AuditFolder TwoStills() =>
        Create("two")
            .Write("alpha.jpg", MediaFixtures.Jpeg(320, 240))
            .Write("bravo.jpg", MediaFixtures.Jpeg(400, 300));

    public static AuditFolder SixStills() =>
        Create("six")
            .Write("alpha.jpg", MediaFixtures.Jpeg(320, 240))
            .Write("bravo.jpg", MediaFixtures.Jpeg(400, 300))
            .Write("charlie.jpg", MediaFixtures.Jpeg(640, 480))
            .Write("delta.jpg", MediaFixtures.Jpeg(800, 600))
            .Write("echo.png", MediaFixtures.Png(512, 384))
            .Write("foxtrot.jpg", MediaFixtures.Jpeg(256, 256));

    /// <summary>
    /// One still and <paramref name="videos"/> videos. SPEC.md § Media policy makes this folder
    /// `still`, so exactly one file is rankable and it never opens (SERVER_SPEC.md § 10.1) — the
    /// counts in `details` are the only thing that says why.
    /// </summary>
    public static AuditFolder OneStillManyVideos(int videos = 8)
    {
        var folder = Create("one-still-many-videos").Write("alpha.jpg", MediaFixtures.Jpeg(320, 240));
        for (var i = 0; i < videos; i++)
            folder.Write($"clip{i}.mp4", MediaFixtures.SmallVideo());
        return folder;
    }

    /// <summary>
    /// Two stills and <paramref name="videos"/> videos: rankable, and one discard away from
    /// `exhausted` while `counts.total` is still large (SERVER_SPEC.md § 7.4.7).
    /// </summary>
    public static AuditFolder TwoStillsManyVideos(int videos = 8)
    {
        var folder = Create("two-stills-many-videos")
            .Write("alpha.jpg", MediaFixtures.Jpeg(320, 240))
            .Write("bravo.jpg", MediaFixtures.Jpeg(400, 300));
        for (var i = 0; i < videos; i++)
            folder.Write($"clip{i}.mp4", MediaFixtures.SmallVideo());
        return folder;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A folder the server still holds .rankmaster.lock in is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The real-media corpus at <c>tests/corpus</c>, or nothing when it has not been built.</summary>
public static class Corpus
{
    private static readonly Lazy<string?> RootPath = new(Locate);

    public static string? Root => RootPath.Value;
    public static bool Available => Root is not null;

    public static byte[] Still(string name) => System.IO.File.ReadAllBytes(System.IO.Path.Combine(Root!, "stills", name));
    public static byte[] Video(string name) => System.IO.File.ReadAllBytes(System.IO.Path.Combine(Root!, "video", name));
    public static byte[] Broken(string name) => System.IO.File.ReadAllBytes(System.IO.Path.Combine(Root!, "broken", name));

    private static string? Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var media = System.IO.Path.Combine(directory.FullName, "tests", "corpus", "media");
            if (Directory.Exists(System.IO.Path.Combine(media, "stills")))
                return media;
            directory = directory.Parent;
        }

        return null;
    }
}
