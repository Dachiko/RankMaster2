using System.Text;

namespace RankMaster2.Server.Tests.Fixtures;

/// <summary>
/// A throwaway folder of real media, built for one test and deleted after it.
///
/// Every profile below is a shape the contract talks about — two files (where `warmPairs` is
/// permanently empty, SERVER_SPEC.md § 9.5), one file (which never opens at all, § 10.1), a mixed
/// folder (stills rank, videos do not, § 7.4.7), a folder of nothing but awkward filenames (§ 11.1).
/// </summary>
public sealed class LibraryFolder : IDisposable
{
    public string Path { get; }

    private LibraryFolder(string path) => Path = path;

    private static LibraryFolder Create(string label)
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "rm2-tests",
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new LibraryFolder(root);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);
    public bool Has(string name) => System.IO.File.Exists(File(name));

    public LibraryFolder Write(string name, byte[] bytes)
    {
        System.IO.File.WriteAllBytes(File(name), bytes);
        return this;
    }

    public LibraryFolder WriteText(string name, string text)
    {
        System.IO.File.WriteAllText(File(name), text, Encoding.UTF8);
        return this;
    }

    public LibraryFolder Subfolder(string name)
    {
        Directory.CreateDirectory(File(name));
        return this;
    }

    // ---- profiles ---------------------------------------------------------------------------

    /// <summary>The smallest folder that opens: exactly two stills. `warmPairs` stays empty here.</summary>
    public static LibraryFolder TwoStills() =>
        Create("two")
            .Write(MediaFixtures.PlainJpeg, MediaFixtures.Jpeg(320, 240))
            .Write(MediaFixtures.SecondJpeg, MediaFixtures.Jpeg(400, 300));

    /// <summary>
    /// Six clean stills — enough for the picker to have choices, so warm pairs can actually fill
    /// and a discard still leaves a rankable folder behind.
    /// </summary>
    public static LibraryFolder SixStills() =>
        Create("six")
            .Write(MediaFixtures.PlainJpeg, MediaFixtures.Jpeg(320, 240))
            .Write(MediaFixtures.SecondJpeg, MediaFixtures.Jpeg(400, 300))
            .Write(MediaFixtures.ThirdJpeg, MediaFixtures.Jpeg(640, 480))
            .Write(MediaFixtures.FourthJpeg, MediaFixtures.Jpeg(800, 600))
            .Write(MediaFixtures.PlainPng, MediaFixtures.Png(512, 384))
            .Write(MediaFixtures.UppercaseExtension, MediaFixtures.Jpeg(256, 256));

    /// <summary>One still. SERVER_SPEC.md § 10.1: this must answer `409 folder_not_rankable`.</summary>
    public static LibraryFolder OneStill() =>
        Create("one").Write(MediaFixtures.PlainJpeg, MediaFixtures.Jpeg(320, 240));

    /// <summary>No media at all — also unrankable, but with zero of both kinds in the details.</summary>
    public static LibraryFolder Empty() => Create("empty");

    /// <summary>Videos only, so `policy` is `video` and the stills endpoints are the wrong kind.</summary>
    public static LibraryFolder VideosOnly() =>
        Create("videos")
            .Write(MediaFixtures.Video, MediaFixtures.SmallVideo())
            .Write("second.mp4", MediaFixtures.SmallVideo());

    /// <summary>
    /// Stills and a video together. SPEC.md § Media policy makes this `still`, and § 7.4.7 warns
    /// that the video stays in `counts.total` and stays fetchable while never appearing in a pair.
    /// </summary>
    public static LibraryFolder Mixed() =>
        Create("mixed")
            .Write(MediaFixtures.PlainJpeg, MediaFixtures.Jpeg(320, 240))
            .Write(MediaFixtures.SecondJpeg, MediaFixtures.Jpeg(400, 300))
            .Write(MediaFixtures.ThirdJpeg, MediaFixtures.Jpeg(640, 480))
            .Write(MediaFixtures.Video, MediaFixtures.SmallVideo());

    /// <summary>
    /// The full menagerie: every awkward name, every awkward byte sequence, both kinds. This is the
    /// folder the media tests run against, so one open covers the whole of § 11 and § 12.
    /// </summary>
    public static LibraryFolder Menagerie()
    {
        var folder = Create("menagerie")
            .Write(MediaFixtures.PlainJpeg, MediaFixtures.Jpeg(320, 240))
            .Write(MediaFixtures.SecondJpeg, MediaFixtures.Jpeg(400, 300))
            .Write(MediaFixtures.ThirdJpeg, MediaFixtures.Jpeg(1600, 1200))
            .Write(MediaFixtures.PlainPng, MediaFixtures.Png(512, 384))
            .Write(MediaFixtures.UppercaseExtension, MediaFixtures.Jpeg(256, 256))
            .Write(MediaFixtures.SpacesAndHash, MediaFixtures.Jpeg(300, 200))
            .Write(MediaFixtures.Unicode, MediaFixtures.Jpeg(300, 200))
            .Write(MediaFixtures.Astral, MediaFixtures.Png(220, 140))
            .Write(MediaFixtures.ExifRotated, MediaFixtures.ExifRotatedJpeg())
            .Write(MediaFixtures.WideGamut, MediaFixtures.WideGamutJpeg())
            .Write(MediaFixtures.WideGamutPng, MediaFixtures.WideGamutPngBytes())
            .Write(MediaFixtures.Large, MediaFixtures.LargeJpeg())
            .Write(MediaFixtures.ZeroByte, Array.Empty<byte>())
            .Write(MediaFixtures.Corrupt, MediaFixtures.CorruptJpeg())
            .Write(MediaFixtures.Truncated, MediaFixtures.TruncatedJpeg())
            .Write(MediaFixtures.Video, MediaFixtures.SmallVideo())
            .WriteText(MediaFixtures.NotMedia, "Not a media file. The scan must not see this.");

        folder.Write(MediaFixtures.MixedCase, MediaFixtures.Jpeg(280, 210));
        if (FilesystemIsCaseSensitive)
            folder.Write(MediaFixtures.LowerCase, MediaFixtures.Jpeg(290, 220));

        return folder;
    }

    /// <summary>A rankable folder whose `rankmaster_db.json` will not parse (SERVER_SPEC.md § 10.1).</summary>
    public static LibraryFolder WithUnreadableDatabase() =>
        SixStills().WriteText("rankmaster_db.json", "{ this is not JSON, and must never be overwritten ");

    // ---- case sensitivity -------------------------------------------------------------------

    private static readonly Lazy<bool> CaseSensitivity = new(ProbeCaseSensitivity);

    /// <summary>
    /// Whether two names differing only by case can coexist here. It decides what "differing only
    /// by case" can even mean for a test: on Windows the pair collapses to one file, and
    /// SERVER_SPEC.md § 11.1 makes id comparison case-insensitive to match.
    /// </summary>
    public static bool FilesystemIsCaseSensitive => CaseSensitivity.Value;

    private static bool ProbeCaseSensitivity()
    {
        var probe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rm2-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probe);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(probe, "Probe.tmp"), "x");
            return !System.IO.File.Exists(System.IO.Path.Combine(probe, "probe.tmp"));
        }
        finally
        {
            try { Directory.Delete(probe, recursive: true); } catch { /* a probe that will not clean up is not a failure */ }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A server that still holds .rankmaster.lock open would fail the delete on Windows.
            // Leaving a temp folder behind is not worth failing a test over; the test's own
            // assertions already say whether the lock was released.
        }
    }
}
