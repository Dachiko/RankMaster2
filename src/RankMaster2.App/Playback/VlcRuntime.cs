using System.IO;
using LibVLCSharp.Shared;

namespace RankMaster2;

/// <summary>One LibVLC instance for the process. Two visible videos share this.</summary>
internal sealed class VlcRuntime : IDisposable
{
    public LibVLC Lib { get; }

    public VlcRuntime()
    {
        var nativeDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        if (!File.Exists(Path.Combine(nativeDir, "libvlc.dll")))
            throw new FileNotFoundException("libvlc.dll not found next to the app.", nativeDir);
        Core.Initialize(nativeDir);
        Lib = new LibVLC(
            "--intf=dummy",
            "--aout=dummy",
            "--no-video-title-show",
            "--no-osd",
            "--no-stats",
            "--quiet",
            "--avcodec-hw=none",
            "--drop-late-frames",
            "--skip-frames");
    }

    public void Dispose()
    {
        try { Lib.Dispose(); }
        catch (Exception)
        {
            // shutting down
        }
    }
}
