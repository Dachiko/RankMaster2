using RankMaster2.Server.Tests.Harness;

namespace RankMaster2.Audit.Conformance.Audit;

/// <summary>
/// The real-media corpus in `tests/corpus/media`. Synthetic fixtures prove the format is handled;
/// these prove the world's files are — real camera EXIF for all eight orientations, a genuine
/// Adobe RGB file paired with an identical untagged one, a 40 MP decode bomb in 612 KB, every
/// accepted container including AV1, and seven deliberately broken files.
///
/// It is git-ignored and built by `tests/corpus/build-corpus.sh`, so every test that needs it skips
/// cleanly rather than failing when it is absent.
/// </summary>
public static class Corpus
{
    public static string Root { get; } = Path.Combine(Repo.Root, "tests", "corpus", "media");

    public static bool Available => Directory.Exists(Path.Combine(Root, "stills"));

    public static string Still(string name) => Path.Combine(Root, "stills", name);
    public static string Video(string name) => Path.Combine(Root, "video", name);
    public static string Broken(string name) => Path.Combine(Root, "broken", name);

    /// <summary>Every EXIF orientation 1–8, landscape and portrait, as real camera files.</summary>
    public static IEnumerable<string> ExifOrientations(string shape) =>
        Enumerable.Range(1, 8).Select(n => $"exif_{shape}_{n}.jpg");

    /// <summary>A throwaway folder holding copies of named corpus files.</summary>
    public static CorpusFolder Folder(string label, params (string Source, string Name)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm2-audit", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (source, name) in files)
            File.Copy(source, Path.Combine(root, name), overwrite: true);
        return new CorpusFolder(root);
    }

    public static CorpusFolder Stills(string label, params string[] names) =>
        Folder(label, names.Select(n => (Still(n), n)).ToArray());

    public static CorpusFolder Videos(string label, params string[] names) =>
        Folder(label, names.Select(n => (Video(n), n)).ToArray());
}

public sealed class CorpusFolder(string path) : IDisposable
{
    public string Path { get; } = path;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public CorpusFolder Add(string sourcePath, string name)
    {
        System.IO.File.Copy(sourcePath, File(name), overwrite: true);
        return this;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A folder the server still holds a lock on is not worth failing a run over.
        }
    }
}
