namespace RankMaster2.Pc.Video.Backend;

using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;

/// <summary>
/// The ONLY file in <c>Video/</c> with <c>using LibVLCSharp.Shared;</c> (D-video.md § 2's layering
/// rule, enforced by a review grep in Phase 4). Ported from the old app's <c>VlcRuntime.cs</c> /
/// <c>VlcFramePlayer.cs</c> with the changes D-video.md § 5.2 names and no others: the worker-thread
/// calling convention is the caller's responsibility (<see cref="VideoEngine"/>), not this class's;
/// the present-coalescing flag lives in <see cref="FrameStore"/>, not here; a <c>Parse</c> step is new
/// (§ 4.5 steps 2/3); <c>--no-stats</c> is removed and <c>Media.Statistics</c> is read.
/// </summary>
public sealed class LibVlcBackend : IPlayerBackend
{
    private LibVLC? _lib;
    private Action<string>? _logSink;

    /// <summary>Set after a successful Initialize: (Core.Initialize, new LibVLC) elapsed time — the
    /// two numbers D-video.md § 4.2's cost table estimates and rm2vidprobe (§ 6.4) measures for real.
    /// Null until Initialize succeeds.</summary>
    public (TimeSpan CoreInitialize, TimeSpan NewLibVlc)? InitTiming { get; private set; }

    /// <summary>The libvlc version string, once Initialize has succeeded — rm2vidprobe's header
    /// line names it (D-video.md § 6.4).</summary>
    public string? Version => _lib?.Version;

    public EngineInitResult Initialize(string? nativeDirectory, VideoOptions options, Action<string> logSink)
    {
        _logSink = logSink;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? pluginsDir = null;
            if (nativeDirectory is not null)
            {
                // The shipped Windows layout: libvlc\win-x64\{libvlc.dll, plugins\...} next to the
                // executable, exactly as the old VlcRuntime.cs resolves it — that resolution is part
                // A's (LibVlcLayout.cs); this class only takes the directory it is given.
                Core.Initialize(nativeDirectory);
                pluginsDir = Path.Combine(nativeDirectory, "plugins");
            }
            else
            {
                // No Windows-style folder: Core.Initialize() with no argument finds the system
                // libvlc.so.5 (D-video.md § 6.2 — the container installs libvlc5/vlc-plugin-base;
                // its own plugin index is used, so --plugin-path is omitted below).
                Core.Initialize();
            }
            var coreInitialize = sw.Elapsed;

            var args = LibVlcOptions.EngineArgs(pluginsDir);
            sw.Restart();
            var lib = new LibVLC(args);
            var newLibVlc = sw.Elapsed;

            lib.Log += OnLog;
            _lib = lib;
            InitTiming = (coreInitialize, newLibVlc);
            return EngineInitResult.Ok();
        }
        catch (Exception ex)
        {
            return EngineInitResult.Failed(ex.Message);
        }
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        // "The last log lines, at or above warning level" (D-video.md § 4.5) — filtered here so
        // VideoLog's ring and every Detail built from it are warning-and-above by construction.
        if (e.Level is LogLevel.Warning or LogLevel.Error)
            _logSink?.Invoke(e.FormattedLog ?? $"[{e.Level}] {e.Module}: {e.Message}");
    }

    public IBackendPlayer CreatePlayer(IPlayerCallbacks callbacks)
    {
        if (_lib is null)
            throw new InvalidOperationException("CreatePlayer was called before a successful Initialize.");
        return new LibVlcPlayer(_lib, callbacks);
    }

    public void Dispose()
    {
        if (_lib is null)
            return;
        _lib.Log -= OnLog;
        try { _lib.Dispose(); }
        catch { /* shutting down */ }
        _lib = null;
    }
}

/// <summary>One MediaPlayer, for one surface's lifetime (D-video.md § 4.4's ownership table).</summary>
internal sealed class LibVlcPlayer : IBackendPlayer
{
    private readonly LibVLC _lib;
    private readonly IPlayerCallbacks _callbacks;
    private readonly MediaPlayer _player;
    private Media? _media;
    private int _generation;

    // libvlc holds native function pointers to these delegates for the player's whole lifetime; a
    // GC'd delegate here is a use-after-free waiting to happen, so each is kept as a field.
    private readonly MediaPlayer.LibVLCVideoFormatCb _onFormat;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _onCleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _onLock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _onDisplay;

    public LibVlcPlayer(LibVLC lib, IPlayerCallbacks callbacks)
    {
        _lib = lib;
        _callbacks = callbacks;

        // The old constructor, verbatim (D-video.md § 4.2).
        _player = new MediaPlayer(lib)
        {
            Mute = true,
            Volume = 0,
            EnableHardwareDecoding = false
        };

        _onFormat = OnFormat;
        _onCleanup = OnCleanup;
        _onLock = OnLock;
        _onDisplay = OnDisplay;
        _player.SetVideoFormatCallbacks(_onFormat, _onCleanup);
        _player.SetVideoCallbacks(_onLock, null, _onDisplay);
        _player.EncounteredError += OnEncounteredError;
        _player.EndReached += OnEndReached;
    }

    public ParseResult Parse(string path, TimeSpan timeout)
    {
        ReleaseMedia();

        var media = new Media(_lib, path, FromType.FromPath);
        _media = media;

        try
        {
            var status = media.Parse(MediaParseOptions.ParseLocal, (int)timeout.TotalMilliseconds)
                .GetAwaiter().GetResult();
            return status switch
            {
                MediaParsedStatus.Done => MapParsed(media),
                MediaParsedStatus.Timeout => ParseResult.TimedOut(),
                _ => ParseResult.Failed()
            };
        }
        catch
        {
            return ParseResult.Failed();
        }
    }

    private static ParseResult MapParsed(Media media)
    {
        foreach (var track in media.Tracks)
        {
            if (track.TrackType == TrackType.Video)
            {
                var video = track.Data.Video;
                return ParseResult.HasVideo((int)video.Width, (int)video.Height, FourCcToString(track.Codec));
            }
        }

        return ParseResult.NoVideoTrack();
    }

    public bool Play(VideoOptions options, int generation)
    {
        if (_media is null)
            return false;

        // The old VlcFramePlayer.Play list, verbatim, plus the optional dav1d thread cap
        // (D-video.md § 5.2).
        foreach (var option in LibVlcOptions.MediaOptions(options))
            _media.AddOption(option);

        _generation = generation;
        return _player.Play(_media);
    }

    public void Restart()
    {
        // § 4.5 step 10 — the belt half of looping. Posted to the worker by the caller; never called
        // from inside a callback (§ 4.6 rule 1).
        try
        {
            _player.Position = 0;
            _player.Play();
        }
        catch
        {
            // loop restart can fail if Stop already ran — the old app's exact tolerance.
        }
    }

    public void Stop()
    {
        try
        {
            var state = _player.State;
            if (state is not VLCState.NothingSpecial and not VLCState.Stopped and not VLCState.Error)
                _player.Stop();
        }
        catch
        {
            // not opened
        }

        ReleaseMedia();
    }

    private void ReleaseMedia()
    {
        // The old Stop() order: player.Media = null, then media.Dispose(). _media is disposed
        // directly (not only via player.Media) so a Media that was Parse()d but never reached
        // player.Play() — a parse failure, no video track, or Play() itself returning false — is
        // still released here rather than leaking until some later, unrelated Parse() call.
        try
        {
            // Clear the player's reference before disposing ours, which is the order the comment
            // above describes and the order native teardown wants.
            //
            // This used to read `if (ReferenceEquals(_player.Media, _media)) _player.Media = null;`
            // — a guard that could never once be true. The getter constructs a **new** managed
            // wrapper on every call (the same library fact A26 turns on, pinned by
            // LibVlcSharpMediaGetterTests), so it never returns the instance we stored: the
            // comparison was always false and the assignment never ran. The player was left holding
            // a native reference to a Media we then disposed, until some later Play() replaced it.
            // Disposing `_media` directly still released our own handle, which is why nothing
            // visibly broke; it is still the wrong order.
            if (_media is not null)
                _player.Media = null;
        }
        catch
        {
            // player already torn down
        }

        try { _media?.Dispose(); }
        catch { /* already disposed */ }

        _media = null;
    }

    public void SetPause(bool paused) => _player.SetPause(paused);

    public BackendStatistics Statistics()
    {
        // A26: MediaPlayer.Media's getter constructs a new managed Media wrapper around a retained
        // native reference on every call (AuditLibVlcSharpMediaGetterTests, kept as the proof of that
        // library fact); this was called once a second per surface and never disposed. `using`
        // disposes the wrapper every call so the native retain this getter takes is released again
        // immediately, instead of leaking one per second per surface.
        using var media = _player.Media;
        if (media is null)
            return default;

        var stats = media.Statistics;
        // D-video.md § 4.3: LoadWatch samples LostPictures/DisplayedPictures.
        return new BackendStatistics(stats.DisplayedPictures, stats.LostPictures);
    }

    public TimeSpan Length => TimeSpan.FromMilliseconds(Math.Max(0, _player.Length));

    public void Dispose()
    {
        _player.EncounteredError -= OnEncounteredError;
        _player.EndReached -= OnEndReached;
        Stop();
        try { _player.Dispose(); }
        catch { /* shutting down */ }
    }

    // ---- native callbacks — VLC's own threads. Never call back into _player from here (§ 4.6 rule 1);
    //      only touch _callbacks, which is VideoSurface posting to FrameStore/the worker/the UI thread. ----

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        WriteFourCc(chroma, "RV32");
        var (w, h, pitch, ln) = _callbacks.OnFormat((int)width, (int)height, _generation);
        width = (uint)w;
        height = (uint)h;
        pitches = (uint)pitch;
        lines = (uint)ln;
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) => _callbacks.OnCleanup(_generation);

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _callbacks.OnLock(_generation));
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture) => _callbacks.OnDisplay(_generation);

    private void OnEncounteredError(object? sender, EventArgs e) => _callbacks.OnEncounteredError(_generation);

    private void OnEndReached(object? sender, EventArgs e) => _callbacks.OnEndReached(_generation);

    private static void WriteFourCc(IntPtr dest, string fourcc) =>
        Marshal.Copy(Encoding.ASCII.GetBytes(fourcc), 0, dest, 4);

    private static string FourCcToString(uint fourcc)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, fourcc);
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
            chars[i] = bytes[i] is >= 0x20 and < 0x7f ? (char)bytes[i] : '?';
        return new string(chars);
    }
}
