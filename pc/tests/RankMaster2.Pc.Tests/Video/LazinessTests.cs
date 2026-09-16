namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Xunit;

/// <summary>D-video.md § 6.1 test 9: constructing <see cref="VideoEngine"/> and probing a stills
/// folder touches nothing native. This half uses <see cref="FakeBackend"/>, so "no libvlc in
/// /proc/self/maps" is necessarily true here — the non-trivial half of this proof is § 6.2's
/// container test 9, which runs the identical shape against the real <c>LibVlcBackend</c>.</summary>
public sealed class LazinessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rm2-video-lazy-").FullName;

    [Fact]
    public void Constructing_the_engine_calls_Initialize_zero_times()
    {
        var backend = new FakeBackend();
        using var engine = new VideoEngine(backend, new SyncUiThread());

        Assert.Equal(0, backend.InitializeCalls);
        Assert.Equal(VideoEngineStatus.Asleep, engine.EngineStatus);
    }

    [Fact]
    public void Classifying_a_stills_folder_and_never_creating_a_surface_calls_Initialize_zero_times()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a.jpg"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_dir, "b.png"), Array.Empty<byte>());

        var backend = new FakeBackend();
        using var engine = new VideoEngine(backend, new SyncUiThread());
        var probe = new MediaProbe();

        var policy = probe.Classify(_dir); // the only thing a stills session touches in Video/

        Assert.Equal(FolderPolicy.Stills, policy);
        Assert.Equal(0, backend.InitializeCalls);
        Assert.Equal(VideoEngineStatus.Asleep, engine.EngineStatus);
    }

    [Fact]
    public void Nothing_in_this_process_has_loaded_libvlc()
    {
        // Trivial with FakeBackend (it never touches libvlc), kept so the same assertion shape
        // exists here and in the container's real-backend version (§ 6.2 row 9).
        if (!OperatingSystem.IsLinux())
            return;

        var maps = File.ReadAllText("/proc/self/maps");
        // "libvlc" alone also matches the managed LibVLCSharp.dll wrapper, which is always loaded
        // (it is referenced); the native library — what laziness is actually about — is always
        // libvlc.so* / libvlccore.so* on Linux.
        Assert.DoesNotContain("libvlc.so", maps, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("libvlccore.so", maps, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
