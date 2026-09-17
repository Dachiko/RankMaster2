using System.Runtime.InteropServices;
using RankMaster2.Pc.Stills;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// SPEC.md section Media policy: "Unreadable / corrupt files are skipped for that pair. The
/// session does not crash." Plan section 3.7, the failure taxonomy.
/// </summary>
public class StillDecoderBrokenTests(ITestOutputHelper output)
{
    private static readonly string? Stills = Corpus.Directory("stills");
    private static readonly string? Broken = Corpus.Directory("broken");

    public static IEnumerable<object[]> BrokenFiles()
    {
        yield return ["empty.jpg"];
        yield return ["header_only.jpg"];
        yield return ["noise.jpg"];
        yield return ["text_pretending.jpg"];
        yield return ["truncated.jpg"];
    }

    [SkippableTheory]
    [MemberData(nameof(BrokenFiles))]
    public void Broken_files_are_refused_cleanly_and_do_not_poison_the_decoder(string name)
    {
        Skip.If(Broken is null, "corpus not built");
        var path = Path.Combine(Broken!, name);
        Skip.If(!File.Exists(path), $"{name} not in the corpus");

        var budget = new DecodeBudget(64L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var result = decoder.Decode(path, 960, 1080);

        if (result.IsSuccess)
        {
            Assert.True(name == "truncated.jpg", $"{name}: only truncated.jpg is allowed to decode as a partial frame, not fail.");
            Assert.True(result.Frame!.IsPartial, $"{name} decoded but was not marked partial.");
            output.WriteLine($"{name}: Ready, IsPartial=true, {result.Frame.Width}x{result.Frame.Height}");
            result.Frame.Release();
        }
        else
        {
            Assert.Equal(StillFailure.NotAnImage, result.Failure);
            Assert.False(string.IsNullOrEmpty(result.Detail));
            Assert.DoesNotContain('\n', result.Detail);
            Assert.DoesNotContain('\r', result.Detail);
            output.WriteLine($"{name}: Failed(NotAnImage, \"{result.Detail}\")");
        }

        Assert.Equal(0, budget.LiveBytes);

        // Nothing is poisoned: the same decoder instance decodes a good file immediately after.
        if (Stills is not null)
        {
            var goodPath = Path.Combine(Stills, "photo_cat.jpg");
            if (File.Exists(goodPath))
            {
                var goodResult = decoder.Decode(goodPath, 960, 1080);
                Assert.True(goodResult.IsSuccess, $"decoder is poisoned after {name}: {goodResult.Failure} {goodResult.Detail}");
                goodResult.Frame!.Release();
            }
        }
    }

    [SkippableFact]
    public void A_video_wearing_a_jpg_extension_is_refused_cleanly()
    {
        Skip.If(Broken is null, "corpus not built");
        var source = Path.Combine(Broken!, "truncated.mp4");
        Skip.If(!File.Exists(source), "truncated.mp4 not in the corpus");

        var tempDir = Directory.CreateTempSubdirectory("rm2-stills-broken-");
        try
        {
            var disguised = Path.Combine(tempDir.FullName, "video_wearing_jpg.jpg");
            File.Copy(source, disguised);

            var budget = new DecodeBudget(64L * 1024 * 1024);
            var decoder = new StillDecoder(budget);
            var result = decoder.Decode(disguised, 960, 1080);

            Assert.False(result.IsSuccess, "a video renamed .jpg should not decode as an image");
            Assert.Equal(StillFailure.NotAnImage, result.Failure);
            output.WriteLine($"truncated.mp4 as .jpg: Failed(NotAnImage, \"{result.Detail}\")");
            Assert.Equal(0, budget.LiveBytes);

            if (Stills is not null)
            {
                var goodPath = Path.Combine(Stills, "photo_cat.jpg");
                if (File.Exists(goodPath))
                {
                    var goodResult = decoder.Decode(goodPath, 960, 1080);
                    Assert.True(goodResult.IsSuccess);
                    goodResult.Frame!.Release();
                }
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_is_Missing()
    {
        var budget = new DecodeBudget(64L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var inRealFolder = decoder.Decode(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.jpg"), 960, 1080);
        Assert.False(inRealFolder.IsSuccess);
        Assert.Equal(StillFailure.Missing, inRealFolder.Failure);

        var inMissingFolder = decoder.Decode(Path.Combine(Path.GetTempPath(), $"no-such-folder-{Guid.NewGuid():N}", "x.jpg"), 960, 1080);
        Assert.False(inMissingFolder.IsSuccess);
        Assert.Equal(StillFailure.Missing, inMissingFolder.Failure);
    }

    [SkippableFact]
    public void An_unreadable_file_is_Unreadable()
    {
        Skip.If(!OperatingSystem.IsLinux(), "this test's chmod 000 trick is Linux-only");
        Skip.If(Environment.UserName == "root" || geteuid() == 0, "running as root, which ignores file permissions");
        Skip.If(Stills is null, "corpus not built");
        var source = Path.Combine(Stills!, "photo_cat.jpg");
        Skip.If(!File.Exists(source), "photo_cat.jpg not in the corpus");

        var tempDir = Directory.CreateTempSubdirectory("rm2-stills-unreadable-");
        try
        {
            var copy = Path.Combine(tempDir.FullName, "locked.jpg");
            File.Copy(source, copy);
            File.SetUnixFileMode(copy, UnixFileMode.None);

            var budget = new DecodeBudget(64L * 1024 * 1024);
            var decoder = new StillDecoder(budget);
            var result = decoder.Decode(copy, 960, 1080);

            Assert.False(result.IsSuccess);
            Assert.Equal(StillFailure.Unreadable, result.Failure);
            output.WriteLine($"chmod 000 file: Failed(Unreadable, \"{result.Detail}\")");
        }
        finally
        {
            try { File.SetUnixFileMode(Path.Combine(tempDir.FullName, "locked.jpg"), UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort cleanup */ }
            tempDir.Delete(recursive: true);
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [SkippableFact]
    public void Too_large_for_the_ceiling_is_TooLarge()
    {
        Skip.If(Stills is null, "corpus not built");
        var path = Path.Combine(Stills!, "bomb_40mp.jpg");
        Skip.If(!File.Exists(path), "bomb_40mp.jpg not in the corpus");

        var budget = new DecodeBudget(4L * 1024 * 1024);
        var decoder = new StillDecoder(budget);

        var result = decoder.Decode(path, 1920, 2160);
        Assert.False(result.IsSuccess);
        Assert.Equal(StillFailure.TooLarge, result.Failure);
        output.WriteLine($"bomb_40mp.jpg at a 4K pane, 4 MB budget: {result.Detail}");
        Assert.Contains("8000", result.Detail);
        Assert.Contains("5000", result.Detail);
        Assert.Equal(0, budget.LiveBytes);
    }
}
