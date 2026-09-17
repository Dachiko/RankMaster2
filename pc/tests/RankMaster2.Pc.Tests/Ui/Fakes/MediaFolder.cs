namespace RankMaster2.Pc.Ui.Tests.Fakes;

/// <summary>
/// A temporary folder with real files in it, for the tests that drive a real
/// <see cref="RankMaster2.Pc.Stills.StillSource"/>: the source stats every id before it decides
/// anything, and reports a file that is not on disk as <c>Missing</c> without ever reaching the
/// decoder.
/// </summary>
public static class MediaFolder
{
    public static string New()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm2-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes <paramref name="name"/> with <paramref name="bytes"/> bytes of filler. The
    /// contents never reach a decoder in these tests; only the name, the length and the mtime do
    /// (they are the still cache's stat key).</summary>
    public static string WriteFile(string folder, string name, int bytes = 64)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public static void Cleanup(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { /* a temporary folder left behind is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }
}
