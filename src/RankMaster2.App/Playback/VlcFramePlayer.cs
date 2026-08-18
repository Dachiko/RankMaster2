using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace RankMaster2;

/// <summary>
/// LibVLC software decode into a WriteableBitmap so the pane stays a normal WPF Image
/// (overlays, clicks, and the info card keep working). Media Foundation cannot play AV1.
/// </summary>
internal sealed class VlcFramePlayer : IDisposable
{
    private readonly LibVLC _lib;
    private readonly LibVLCSharp.Shared.MediaPlayer _player;
    private readonly Dispatcher _dispatcher;
    private readonly object _copyGate = new();

    private int _generation;
    private bool _suppressError;
    private bool _disposed;

    private IntPtr _buffer = IntPtr.Zero;
    private int _bufferSize;
    private int _width;
    private int _height;
    private int _stride;
    private byte[]? _front;
    private byte[]? _back;
    private WriteableBitmap? _bitmap;

    public VlcFramePlayer(VlcRuntime runtime, Dispatcher dispatcher)
    {
        _lib = runtime.Lib;
        _dispatcher = dispatcher;
        _player = new LibVLCSharp.Shared.MediaPlayer(_lib)
        {
            Mute = true,
            Volume = 0,
            EnableHardwareDecoding = false
        };
        _player.SetVideoFormatCallbacks(OnFormat, OnCleanup);
        _player.SetVideoCallbacks(OnLock, null, OnDisplay);
        _player.EncounteredError += OnEncounteredError;
        _player.Buffering += OnBuffering;
        _player.EndReached += OnEndReached;
    }

    public bool Opened { get; private set; }
    public string? ExpectedPath { get; private set; }
    public float BufferPercent { get; private set; }
    public int PanelWidth { get; set; } = 960;
    public int PanelHeight { get; set; } = 1080;

    public event Action<BitmapSource>? FirstFrame;
    public event Action<string>? Failed;

    public void SetPanelSize(int width, int height)
    {
        if (width >= 16) PanelWidth = width;
        if (height >= 16) PanelHeight = height;
    }

    public void Play(string path)
    {
        if (Opened && PathsMatch(ExpectedPath, path))
            return;

        Stop();
        ExpectedPath = path;
        BufferPercent = 0;
        Opened = false;

        try
        {
            using var media = new Media(_lib, path, FromType.FromPath);
            media.AddOption(":no-audio");
            media.AddOption(":input-repeat=65535");
            media.AddOption(":avcodec-hw=none");
            if (!_player.Play(media))
                RaiseFailed(path);
        }
        catch (Exception)
        {
            RaiseFailed(path);
        }
    }

    public void Stop()
    {
        _generation++;
        Opened = false;
        BufferPercent = 0;
        ExpectedPath = null;
        _suppressError = true;
        try
        {
            var state = _player.State;
            if (state is not VLCState.NothingSpecial and not VLCState.Stopped and not VLCState.Error)
                _player.Stop();
        }
        catch (Exception)
        {
            // not opened
        }
        finally
        {
            try
            {
                var leftover = _player.Media;
                _player.Media = null;
                leftover?.Dispose();
            }
            catch
            {
                // player already torn down
            }

            _suppressError = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _player.EncounteredError -= OnEncounteredError;
        _player.Buffering -= OnBuffering;
        _player.EndReached -= OnEndReached;
        Stop();
        try { _player.Dispose(); }
        catch (Exception)
        {
            // shutting down
        }
        FreeBuffer();
    }

    private uint OnFormat(
        ref IntPtr opaque,
        IntPtr chroma,
        ref uint width,
        ref uint height,
        ref uint pitches,
        ref uint lines)
    {
        WriteFourCc(chroma, "RV32");
        Fit(ref width, ref height);
        width = Math.Max(2, width & ~1u);
        height = Math.Max(2, height & ~1u);
        pitches = Align32(width * 4);
        lines = Align32(height);

        lock (_copyGate)
        {
            FreeBufferUnlocked();
            _width = (int)width;
            _height = (int)height;
            _stride = (int)pitches;
            _bufferSize = (int)(pitches * lines);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
            _front = new byte[_bufferSize];
            _back = new byte[_bufferSize];
        }

        var w = _width;
        var h = _height;
        var gen = _generation;
        _dispatcher.BeginInvoke(() =>
        {
            if (gen != _generation)
                return;
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        });

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) => FreeBuffer();

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_buffer == IntPtr.Zero)
            return;
        var gen = _generation;
        lock (_copyGate)
        {
            if (_back is null || _buffer == IntPtr.Zero)
                return;
            Marshal.Copy(_buffer, _back, 0, _bufferSize);
            (_front, _back) = (_back, _front);
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(gen));
    }

    private void Present(int gen)
    {
        if (gen != _generation || _bitmap is null)
            return;

        byte[]? front;
        lock (_copyGate)
            front = _front;
        if (front is null)
            return;

        _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), front, _stride, 0);
        if (Opened)
            return;

        Opened = true;
        BufferPercent = 100;
        FirstFrame?.Invoke(_bitmap);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        var path = ExpectedPath;
        var gen = _generation;
        if (_suppressError || path is null)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            if (gen != _generation || ExpectedPath != path)
                return;
            RaiseFailed(path);
        });
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) =>
        BufferPercent = e.Cache;

    private void OnEndReached(object? sender, EventArgs e)
    {
        var path = ExpectedPath;
        var gen = _generation;
        _dispatcher.BeginInvoke(() =>
        {
            if (gen != _generation || ExpectedPath != path || path is null)
                return;
            try
            {
                _player.Position = 0;
                _player.Play();
            }
            catch (Exception)
            {
                // loop restart can fail if Stop already ran
            }
        });
    }

    private void RaiseFailed(string path) => Failed?.Invoke(path);

    private void Fit(ref uint width, ref uint height)
    {
        var maxW = (uint)Math.Max(16, PanelWidth);
        var maxH = (uint)Math.Max(16, PanelHeight);
        if (width <= maxW && height <= maxH)
            return;
        var scale = Math.Min(maxW / (double)width, maxH / (double)height);
        width = Math.Max(2, (uint)Math.Round(width * scale));
        height = Math.Max(2, (uint)Math.Round(height * scale));
    }

    private void FreeBuffer()
    {
        lock (_copyGate)
            FreeBufferUnlocked();
    }

    private void FreeBufferUnlocked()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }

        _front = null;
        _back = null;
        _bufferSize = 0;
    }

    private static uint Align32(uint size) => (size + 31u) & ~31u;

    private static void WriteFourCc(IntPtr dest, string fourcc) =>
        Marshal.Copy(Encoding.ASCII.GetBytes(fourcc), 0, dest, 4);

    private static bool PathsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
