namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// COPIED from the bottom of tests/RankMaster2.Server.Tests/Media/StillRendererCorpusTests.cs
/// (plan section 4). Locates <c>tests/corpus/media</c>, which is git-ignored and may not be built.
/// </summary>
internal static class Corpus
{
    private static readonly Lazy<string?> Root = new(Find);

    public static string? Directory(string group)
    {
        if (Root.Value is null)
            return null;

        var path = Path.Combine(Root.Value, group);
        return System.IO.Directory.Exists(path) ? path : null;
    }

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "corpus", "media");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
