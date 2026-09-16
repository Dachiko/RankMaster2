namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Xunit;

/// <summary>D-video.md § 6.1 test 8: SPEC.md § Media policy, by extension only.</summary>
public class MediaProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rm2-mediaprobe-").FullName;
    private readonly MediaProbe _probe = new();

    private void Touch(string name) => File.WriteAllBytes(Path.Combine(_dir, name), Array.Empty<byte>());

    [Fact]
    public void Stills_only_is_Stills()
    {
        Touch("a.jpg");
        Touch("b.PNG"); // extension case-insensitive
        Assert.Equal(FolderPolicy.Stills, _probe.Classify(_dir));
    }

    [Fact]
    public void Videos_only_is_Videos()
    {
        Touch("a.mp4");
        Touch("b.MKV");
        Assert.Equal(FolderPolicy.Videos, _probe.Classify(_dir));
    }

    [Fact]
    public void Mixed_folder_is_Stills()
    {
        Touch("a.jpg");
        Touch("b.mp4");
        Assert.Equal(FolderPolicy.Stills, _probe.Classify(_dir));
    }

    [Fact]
    public void Empty_folder_is_Empty()
    {
        Assert.Equal(FolderPolicy.Empty, _probe.Classify(_dir));
    }

    [Fact]
    public void Folder_of_unrecognised_extensions_is_Empty()
    {
        Touch("readme.txt");
        Touch("notes.json");
        Assert.Equal(FolderPolicy.Empty, _probe.Classify(_dir));
    }

    [Fact]
    public void Missing_folder_is_Empty_and_never_throws()
    {
        Assert.Equal(FolderPolicy.Empty, _probe.Classify(Path.Combine(_dir, "does-not-exist")));
    }

    [Theory]
    [InlineData("video", true)]
    [InlineData("Video", true)]
    [InlineData("still", false)]
    [InlineData("", false)]
    public void NeedsVideoEngine_matches_the_snapshot_policy_string(string policy, bool expected)
    {
        Assert.Equal(expected, _probe.NeedsVideoEngine(policy));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
