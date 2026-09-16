using System.Runtime.InteropServices;

namespace RankMaster2.Pc.Stills;

// COPIED from src/RankMaster2.Server/Media/DecodeBudget.cs (plan section 4: "copy verbatim,
// namespace changed only"). Do not add behaviour here; the wait-and-retry rule in plan section
// 3.3 ("if live + n > Ceiling and live > 0 -> wait ... and retry") is DecodeQueue's business, not
// this budget's -- exactly as it is on the server, where the caller (StillRenderer) decides what
// to do with DecodeBudgetExceededException.

/// <summary>
/// Thrown when a decode asks for more pixel memory than this part is willing to hold at once.
/// Surfaces as <see cref="StillFailure.TooLarge"/>: the file cannot be decoded inside the budget,
/// which is the honest answer.
/// </summary>
public sealed class DecodeBudgetExceededException(long requested, long live, long ceiling)
    : Exception($"A decode asked for {requested:N0} bytes on top of {live:N0} already held, which would exceed the {ceiling:N0} byte ceiling.")
{
    public long RequestedBytes { get; } = requested;
    public long LiveBytes { get; } = live;
    public long CeilingBytes { get; } = ceiling;
}

/// <summary>
/// A hard ceiling on pixel memory, and the reason the memory guarantee in this layer is testable.
/// <para/>
/// SkiaSharp decodes into native memory and offers no global allocation limit. So this layer
/// supplies the pixel buffers itself: every large allocation on the still path goes through
/// <see cref="Allocate"/>, is handed to Skia with <c>SKBitmap.InstallPixels</c> or
/// <c>SKSurface.Create</c>, and is counted here.
/// <para/>
/// The ceiling is enforced <b>before</b> the memory is taken rather than after, and
/// <see cref="PeakBytes"/> records the true high-water mark, so a test can assert the actual
/// number of bytes a decode cost instead of asserting only that it finished.
/// <para/>
/// What this does <b>not</b> count is whatever Skia allocates internally -- its Huffman tables,
/// row buffers and the like. Those are bounded by the codec and by image width, not by pixel
/// count, so they do not scale with the thing this ceiling exists to bound. The destination
/// buffer is the allocation that grows with megapixels, and the destination buffer is ours.
/// </summary>
public sealed class DecodeBudget(long ceilingBytes)
{
    private readonly object _gate = new();
    private long _live;
    private long _peak;

    public long CeilingBytes { get; } = ceilingBytes > 0
        ? ceilingBytes
        : throw new ArgumentOutOfRangeException(nameof(ceilingBytes), ceilingBytes, "A budget needs a positive ceiling.");

    /// <summary>Bytes currently held by live buffers.</summary>
    public long LiveBytes
    {
        get { lock (_gate) return _live; }
    }

    /// <summary>The largest <see cref="LiveBytes"/> has been since the last <see cref="ResetPeak"/>.</summary>
    public long PeakBytes
    {
        get { lock (_gate) return _peak; }
    }

    public void ResetPeak()
    {
        lock (_gate) _peak = _live;
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> of native memory, or refuses. The returned buffer must be
    /// disposed; disposing returns its bytes to the budget.
    /// </summary>
    public BudgetedBuffer Allocate(long bytes)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "A buffer needs a positive length.");

        lock (_gate)
        {
            if (_live + bytes > CeilingBytes)
                throw new DecodeBudgetExceededException(bytes, _live, CeilingBytes);

            // Reserve before allocating, so two threads cannot both pass the check.
            _live += bytes;
            if (_live > _peak)
                _peak = _live;
        }

        try
        {
            var pointer = Marshal.AllocHGlobal((nint)bytes);
            return new BudgetedBuffer(this, pointer, bytes);
        }
        catch
        {
            Release(bytes);
            throw;
        }
    }

    internal void Release(long bytes)
    {
        lock (_gate) _live -= bytes;
    }
}

/// <summary>One reserved native buffer. Disposing it frees the memory and refunds the budget.</summary>
public sealed class BudgetedBuffer : IDisposable
{
    private readonly DecodeBudget _budget;
    private IntPtr _pointer;

    internal BudgetedBuffer(DecodeBudget budget, IntPtr pointer, long length)
    {
        _budget = budget;
        _pointer = pointer;
        Length = length;
    }

    public IntPtr Pointer => _pointer != IntPtr.Zero
        ? _pointer
        : throw new ObjectDisposedException(nameof(BudgetedBuffer));

    public long Length { get; }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            return;

        Marshal.FreeHGlobal(pointer);
        _budget.Release(Length);
    }
}
