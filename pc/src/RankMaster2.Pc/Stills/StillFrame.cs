namespace RankMaster2.Pc.Stills;

/// <summary>
/// An upright, sRGB, BGRA8888 premultiplied image at (or below) pane size. Plan section 3.6:
/// reference-counted so eviction can never pull the buffer out from under a caller mid-copy.
/// <para/>
/// The count starts at one, representing the cache's own reference (the constructor is called by
/// whatever just decoded the frame and is about to hand it to the cache). Each
/// <see cref="StillLease"/> adds one more and removes it on <see cref="StillLease.Dispose"/>; when
/// the cache evicts the frame it releases its own reference the same way. The buffer is freed
/// exactly when the count reaches zero, wherever that happens.
/// </summary>
public sealed class StillFrame
{
    private readonly BudgetedBuffer _buffer;
    private int _refs = 1;

    internal StillFrame(
        string id,
        BudgetedBuffer buffer,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        bool isPartial)
    {
        Id = id;
        _buffer = buffer;
        Width = width;
        Height = height;
        RowBytes = width * 4;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        IsPartial = isPartial;
    }

    public string Id { get; }

    /// <summary>Displayed width, after orientation, &lt;= pane width, &lt;= 4096 long edge.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Always <c>Width * 4</c> -- BGRA8888.</summary>
    public int RowBytes { get; }

    /// <summary>Valid while any lease (including the cache's own) is alive; throws afterwards.</summary>
    public IntPtr Pixels => _buffer.Pointer;

    /// <summary>The file's displayed size, after orientation (what MediaMeta.width/height would report).</summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Decode ended with IncompleteInput; rows past the cut are whatever Skia left.</summary>
    public bool IsPartial { get; }

    public long ByteSize => (long)RowBytes * Height;

    /// <summary>Takes out a new lease on this frame. Internal: callers go through <see cref="StillLease"/>'s public constructor path via the cache/source, never construct a frame directly.</summary>
    internal StillLease Lease() => new(this);

    internal void AddRef() => Interlocked.Increment(ref _refs);

    /// <summary>Drops one reference (a lease's, or the cache's own). Frees the buffer at zero.</summary>
    internal void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
            _buffer.Dispose();
    }
}
