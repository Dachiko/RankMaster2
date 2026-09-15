using System.Text;
using RankMaster2.Server.Tests.Fixtures;

namespace RankMaster2.Audit.Compatibility.Support;

/// <summary>
/// A throwaway media folder this audit controls end to end. It exists alongside
/// <see cref="LibraryFolder"/> because a compatibility test needs to choose its own filenames —
/// the long ones, the case-colliding ones, the ones copied out of the real corpus.
/// </summary>
public sealed class Scratch : IDisposable
{
    public string Path { get; }

    private Scratch(string path) => Path = path;

    public static Scratch New(string label = "audit")
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rm2-audit", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new Scratch(root);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);
    public bool Has(string name) => System.IO.File.Exists(File(name));
    public bool HasIn(string subfolder, string name) =>
        System.IO.File.Exists(System.IO.Path.Combine(Path, subfolder, name));

    public string[] TopLevelNames() =>
        Directory.GetFiles(Path).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public string[] NamesIn(string subfolder)
    {
        var dir = System.IO.Path.Combine(Path, subfolder);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir).Select(p => System.IO.Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : [];
    }

    public Scratch Write(string name, byte[] bytes)
    {
        System.IO.File.WriteAllBytes(File(name), bytes);
        return this;
    }

    public Scratch WriteText(string name, string text)
    {
        System.IO.File.WriteAllText(File(name), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return this;
    }

    /// <summary>A real, decodable JPEG, sized so no two fixtures share a fingerprint.</summary>
    public Scratch Jpeg(string name, int width = 320, int height = 240) =>
        Write(name, MediaFixtures.Jpeg(width, height));

    public Scratch Png(string name, int width = 256, int height = 192) =>
        Write(name, MediaFixtures.Png(width, height));

    public Scratch Video(string name) => Write(name, MediaFixtures.SmallVideo());

    /// <summary>n distinct stills named a01.jpg … — enough for the picker to have real choices.</summary>
    public Scratch Stills(int n, string prefix = "s")
    {
        for (var i = 0; i < n; i++)
            Jpeg($"{prefix}{i:D2}.jpg", 200 + (i * 7), 150 + (i * 5));
        return this;
    }

    public void Delete(string name) => System.IO.File.Delete(File(name));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A stranded temp folder is not worth failing a run over; the assertions already said
            // whether the server released what it was holding.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
