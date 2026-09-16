using System.Diagnostics;
using RankMaster2;
using RankMaster2.Catalog;
using RankMaster2.Pc.Stills;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

public class MediaProbeTests(ITestOutputHelper output)
{
    private static string NewTempFolder() => Directory.CreateTempSubdirectory("rm2-mediaprobe-").FullName;

    private static void Touch(string folder, string name) =>
        File.WriteAllBytes(Path.Combine(folder, name), []);

    [Fact]
    public async Task Stills_only()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.jpg");
            Touch(folder, "b.png");
            Touch(folder, "c.webp");

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            Assert.Equal(new FolderMedia(3, 0), media);
            Assert.Equal(MediaKind.Still, media.Policy);
            Assert.False(media.NeedsVideoEngine);
            Assert.False(media.HasVideo);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Videos_only_two_files_need_the_engine()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.mp4");
            Touch(folder, "b.webm");

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            Assert.Equal(new FolderMedia(0, 2), media);
            Assert.Equal(MediaKind.Video, media.Policy);
            Assert.True(media.NeedsVideoEngine);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Videos_only_one_file_does_not_need_the_engine()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.mp4");

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            Assert.Equal(new FolderMedia(0, 1), media);
            Assert.Equal(MediaKind.Video, media.Policy);
            Assert.False(media.NeedsVideoEngine);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_folder_is_stills_only_by_policy()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.jpg");
            Touch(folder, "b.mp4");

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            Assert.Equal(new FolderMedia(1, 1), media);
            Assert.Equal(MediaKind.Still, media.Policy);
            Assert.True(media.HasVideo);
            Assert.False(media.NeedsVideoEngine);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_file_tmp_files_subfolders_and_hidden_files_are_not_counted()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.jpg");
            Touch(folder, "rankmaster_db.json");
            Touch(folder, "partial.tmp");
            var sub = Directory.CreateDirectory(Path.Combine(folder, "subfolder"));
            Touch(sub.FullName, "b.jpg"); // must not be counted -- top level only

            if (OperatingSystem.IsWindows())
            {
                var hiddenPath = Path.Combine(folder, "hidden.jpg");
                Touch(folder, "hidden.jpg");
                File.SetAttributes(hiddenPath, FileAttributes.Hidden);
            }
            else
            {
                // .NET on Unix derives FileAttributes.Hidden from a leading dot in the filename;
                // File.SetAttributes(..., Hidden) on a non-dotfile has no effect (measured here),
                // so this case is Windows-only, exactly as plan section 6 expects ("on Linux skip
                // that case").
                output.WriteLine("skipped: FileAttributes.Hidden is not settable on an arbitrary filename on this OS");
            }

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            Assert.Equal(1, media.Stills); // only a.jpg
            Assert.Equal(0, media.Videos);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_extensions_are_not_counted()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "notes.txt");
            Touch(folder, "photo.heic");
            Touch(folder, "real.jpg");

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            Assert.Equal(new FolderMedia(1, 0), media);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_folder_is_zero()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm2-mediaprobe-missing-{Guid.NewGuid():N}");
        var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);
        Assert.Equal(new FolderMedia(0, 0), media);
    }

    [Fact]
    public async Task An_unreadable_folder_throws()
    {
        if (!OperatingSystem.IsLinux())
        {
            output.WriteLine("skipped: this test's chmod 000 trick is Linux-only");
            return;
        }
        if (Environment.UserName == "root")
        {
            output.WriteLine("skipped: running as root, which ignores directory permissions");
            return;
        }

        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.jpg");
            File.SetUnixFileMode(folder, UnixFileMode.None);

            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
                () => new MediaProbe().ProbeAsync(folder, CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Twenty_thousand_names_probe_under_half_a_second()
    {
        var folder = NewTempFolder();
        try
        {
            for (var i = 0; i < 20_000; i++)
                Touch(folder, i % 2 == 0 ? $"still_{i:D6}.jpg" : $"video_{i:D6}.mp4");

            var sw = Stopwatch.StartNew();
            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);
            sw.Stop();

            output.WriteLine($"20,000 files probed in {sw.ElapsedMilliseconds} ms: {media.Stills} stills, {media.Videos} videos");
            Assert.Equal(new FolderMedia(10_000, 10_000), media);
            Assert.True(sw.ElapsedMilliseconds < 500, $"expected under 500 ms here, took {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Agrees_with_the_catalog()
    {
        var folder = NewTempFolder();
        try
        {
            Touch(folder, "a.jpg");
            Touch(folder, "b.png");
            Touch(folder, "c.mp4");
            Touch(folder, "d.webm");
            // A real, parseable (if empty) catalog file -- JsonCatalog.Scan reads it, unlike
            // MediaProbe, which only ever looks at the name.
            File.WriteAllText(Path.Combine(folder, "rankmaster_db.json"), """{"version":1,"lastUpdated":0,"images":{}}""");
            Touch(folder, "e.tmp");
            Touch(folder, "notes.txt");
            var sub = Directory.CreateDirectory(Path.Combine(folder, "sub"));
            Touch(sub.FullName, "f.jpg");

            var media = await new MediaProbe().ProbeAsync(folder, CancellationToken.None);

            var catalogRecords = new JsonCatalog().Scan(folder);
            var catalogStills = catalogRecords.Count(r => r.Kind == MediaKind.Still);
            var catalogVideos = catalogRecords.Count(r => r.Kind == MediaKind.Video);

            Assert.Equal(catalogStills, media.Stills);
            Assert.Equal(catalogVideos, media.Videos);
            Assert.Equal(JsonCatalog.Classify(catalogRecords.Select(r => r.Kind)), media.Policy);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
