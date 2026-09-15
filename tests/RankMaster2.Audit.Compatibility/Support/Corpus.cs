using RankMaster2.Server.Tests.Harness;

namespace RankMaster2.Audit.Compatibility.Support;

/// <summary>
/// The real-media corpus at <c>tests/corpus/</c> — real cameras, real encoders, real containers.
/// It is git-ignored and built by <c>tests/corpus/build-corpus.sh</c>, so everything here degrades
/// to "not available" rather than failing when it has not been built.
/// </summary>
public static class Corpus
{
    private static readonly Lazy<string?> RootPath = new(Locate);

    public static string? Root => RootPath.Value;

    public static bool Available => Root is not null;

    /// <summary>Printed by any test that had to stand down, so an absent corpus is never silent.</summary>
    public const string Missing =
        "SKIPPED: the real-media corpus is not built. Run tests/corpus/build-corpus.sh (needs curl, " +
        "ffmpeg and python3 with Pillow) to make this test do anything.";

    public static string[] Stills() => Files("stills");
    public static string[] Videos() => Files("video");
    public static string[] Broken() => Files("broken");

    private static string[] Files(string group)
    {
        if (Root is null) return [];
        var dir = Path.Combine(Root, group);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : [];
    }

    /// <summary>Copy a corpus file into a scratch folder under a name of the caller's choosing.</summary>
    public static void CopyAs(string sourcePath, Scratch target, string name) =>
        File.Copy(sourcePath, target.File(name), overwrite: true);

    private static string? Locate()
    {
        string root;
        try
        {
            root = Repo.Root;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var media = Path.Combine(root, "tests", "corpus", "media");
        if (!Directory.Exists(media)) return null;

        // A half-built corpus is worse than none: it would silently narrow what a test covers.
        var stills = Path.Combine(media, "stills");
        return Directory.Exists(stills) && Directory.GetFiles(stills).Length >= 8 ? media : null;
    }
}
