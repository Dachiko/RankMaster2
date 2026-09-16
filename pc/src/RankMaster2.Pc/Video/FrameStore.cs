namespace RankMaster2.Pc.Video;

using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Pure with respect to VLC: one native buffer VLC's vout thread writes into (<see cref="Allocate"/>,
/// <see cref="NativePointer"/>), plus a front/back pair of managed byte arrays and the
/// one-pending-present flag D-video.md § 4.1 adds on top of the old app's buffer code.
///
/// Not reentrant across <see cref="Allocate"/> vs <see cref="Free"/> from two threads at once beyond
/// what the internal lock gives — callers still honour the engine's single-worker-thread rule for
/// anything that reaches VLC; this class only protects the buffer itself.
/// </summary>
public sealed class FrameStore
{
    private readonly object _gate = new();
    private IntPtr _native;
    private int _bufferSize;
    private byte[]? _front;
    private byte[]? _back;
    private int _presentPending;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }

    public bool IsAllocated
    {
        get { lock (_gate) return _native != IntPtr.Zero; }
    }

    /// <summary>
    /// OnFormat: (re)allocates for a new size, freeing any previous buffer first. Returns the native
    /// pointer OnLock will later hand to VLC.
    ///
    /// <paramref name="nativeLines"/> is the number of rows VLC was told exist (<c>Align32(height)</c>
    /// — OnFormat's <c>lines</c> out-param), which can be taller than <paramref name="height"/> once
    /// 32-aligned; the buffer must be sized against it or VLC's vout thread writes past the end of the
    /// allocation into the next heap block. Defaults to <paramref name="height"/> for callers (and
    /// tests) that already pass an aligned value.
    /// </summary>
    public IntPtr Allocate(int width, int height, int stride, int? nativeLines = null)
    {
        lock (_gate)
        {
            FreeUnlocked();
            Width = width;
            Height = height;
            Stride = stride;
            _bufferSize = checked(stride * (nativeLines ?? height));
            _native = Marshal.AllocHGlobal(_bufferSize);
            _front = new byte[_bufferSize];
            _back = new byte[_bufferSize];
            return _native;
        }
    }

    /// <summary>OnLock: the pointer VLC decodes into for this frame.</summary>
    public IntPtr NativePointer
    {
        get { lock (_gate) return _native; }
    }

    /// <summary>
    /// OnDisplay: copies native → back, swaps front/back so the newest decoded frame is what
    /// <see cref="BeginPresent"/> will read. Returns true the caller should post a Present (no
    /// Present was already queued for this surface); false if one is already in flight, in which case
    /// the frame just written is still picked up by that pending Present once it runs — nothing is
    /// lost, only coalesced, per § 4.1's "one pending present per surface" rule.
    /// </summary>
    public bool OnDisplay()
    {
        lock (_gate)
        {
            if (_native == IntPtr.Zero || _back is null)
                return false; // freed already (Stop/Dispose raced a late OnDisplay) — no write, no post
            Marshal.Copy(_native, _back, 0, _bufferSize);
            (_front, _back) = (_back, _front);
        }

        return Interlocked.Exchange(ref _presentPending, 1) == 0;
    }

    /// <summary>UI thread, top of Present: clears the pending flag and returns the newest front
    /// buffer, or null if nothing is allocated (a stale/late call after Free).</summary>
    public byte[]? BeginPresent()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        lock (_gate)
            return _front;
    }

    /// <summary>OnCleanup, StopAsync, Dispose: frees the native buffer and drops both managed copies.
    /// Idempotent; safe to call from any thread.</summary>
    public void Free()
    {
        lock (_gate)
            FreeUnlocked();
    }

    private void FreeUnlocked()
    {
        if (_native != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_native);
            _native = IntPtr.Zero;
        }

        _front = null;
        _back = null;
        _bufferSize = 0;
        Width = Height = Stride = 0;
        Interlocked.Exchange(ref _presentPending, 0);
    }
}
