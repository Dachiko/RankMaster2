using System.Diagnostics;

namespace RankMaster2.Pc.Stills;

/// <summary>
/// The half of pc/plans/C-stills.md § 3.3's memory rule that <see cref="DecodeBudget"/> deliberately
/// does not implement -- "if <c>live + n &gt; Ceiling</c> and <c>live &gt; 0</c>, wait (the other
/// decode will finish in well under a second) and retry". Without it, two visible decodes running at
/// once share one ceiling with no coordination at all, and whichever asks second is told its file
/// cannot be decoded. That is AUDIT2.md § 2.1 on the server, where the same two settings
/// (<c>MaxConcurrentDecodes = 2</c> against one <c>DecodeMemoryLimitMegabytes</c>) made one of every
/// two large PNGs fail, 12 times out of 12; this part is built the same way -- two worker threads
/// (<see cref="DecodeQueue"/>), one <see cref="DecodeBudget"/> -- and the product's whole interaction
/// is showing a pair, so the two decodes that collide are exactly the two pictures on screen.
/// <para/>
/// <b>Reservations, not retries.</b> A decode reserves its whole worst-case peak here BEFORE it
/// allocates anything, and holds the reservation until it is finished. That is what makes waiting
/// safe: a waiter holds no memory, so two decodes can never sit waiting for each other's buffers.
/// A plain "allocate, and wait if it does not fit" would deadlock exactly there -- each decode
/// holding its decode buffer and waiting for room to grow.
/// <para/>
/// <b>What it costs.</b> Admission compares <c>LiveBytes + outstanding reservations + this one</c>
/// with the ceiling, and <c>LiveBytes</c> already includes whatever the outstanding reservations
/// have actually spent, so the test is conservative: two decodes whose peaks would in fact have fitted
/// side by side can be made to run one after the other. Slower, never wrong -- and never the thing
/// this exists to prevent, which is a real photograph reported as a file that will not decode.
/// <para/>
/// A request larger than the whole ceiling is refused at once and does not wait: no amount of waiting
/// makes room that does not exist. That is the deliberate, documented ceiling of § 3.3 (the 200 MP
/// PNG), and it reaches the owner as <see cref="StillFailure.TooLarge"/> with its own sentence, not
/// as "not a valid image".
/// </summary>
internal sealed class DecodeRoom
{
    /// <summary>Backstop only. A reservation released by another decode pulses the wait immediately,
    /// so this expires only if a decode is stuck for far longer than any decode takes; the honest
    /// answer then is the same one a single oversized file gets. It is bounded for a reason:
    /// <c>StillSource.ReleaseAllAsync</c> waits for in-flight decodes, so a wait that never ended
    /// would be a folder that never closed.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly DecodeBudget _budget;
    private readonly TimeSpan _maxWait;
    private readonly TimeSpan _poll;
    private long _reserved;
    private TimeSpan _lastWait;
    private int _waited;

    public DecodeRoom(DecodeBudget budget, TimeSpan? maxWait = null, TimeSpan? poll = null)
    {
        _budget = budget;
        _maxWait = maxWait ?? DefaultMaxWait;
        // A frame freed elsewhere (an eviction, a pane letting go of its lease) lowers LiveBytes
        // without passing through this class, so a waiter also re-checks on a timer rather than
        // relying on a pulse it would never get.
        _poll = poll ?? TimeSpan.FromMilliseconds(20);
    }

    /// <summary>Bytes currently promised to decodes that have not finished.</summary>
    public long ReservedBytes { get { lock (_gate) return _reserved; } }

    /// <summary>How long the last <see cref="Take"/> spent waiting for room.</summary>
    public TimeSpan LastWait { get { lock (_gate) return _lastWait; } }

    /// <summary>How many decodes have had to wait for another to finish. Monotonic, so it is the
    /// honest answer to "was this pair serialised rather than refused?" -- which is the whole of
    /// AUDIT2.md § 2.1's remedy.</summary>
    public int WaitedAdmissions { get { lock (_gate) return _waited; } }

    /// <summary>
    /// Reserves <paramref name="peakBytes"/> of headroom, waiting for another decode to finish if
    /// that is what it takes. Throws <see cref="DecodeBudgetExceededException"/> only when the request
    /// cannot fit inside an empty budget, or when the wait ran out.
    /// </summary>
    public Reservation Take(long peakBytes)
    {
        if (peakBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(peakBytes), peakBytes, "A decode reserves a positive number of bytes.");

        if (peakBytes > _budget.CeilingBytes)
            throw new DecodeBudgetExceededException(peakBytes, 0, _budget.CeilingBytes);

        var clock = Stopwatch.StartNew();
        var waited = false;
        lock (_gate)
        {
            while (true)
            {
                var live = _budget.LiveBytes;
                if (live + _reserved + peakBytes <= _budget.CeilingBytes)
                {
                    _reserved += peakBytes;
                    _lastWait = clock.Elapsed;
                    if (waited) _waited++;
                    return new Reservation(this, peakBytes);
                }

                var left = _maxWait - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    _lastWait = clock.Elapsed;
                    throw new DecodeBudgetExceededException(peakBytes, live + _reserved, _budget.CeilingBytes);
                }

                waited = true;
                Monitor.Wait(_gate, left < _poll ? left : _poll);
            }
        }
    }

    private void Give(long bytes)
    {
        lock (_gate)
        {
            _reserved -= bytes;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>One decode's headroom. Disposed when that decode is done, whatever the outcome.</summary>
    internal sealed class Reservation : IDisposable
    {
        private DecodeRoom? _room;

        internal Reservation(DecodeRoom room, long bytes)
        {
            _room = room;
            Bytes = bytes;
        }

        public long Bytes { get; }

        public void Dispose()
        {
            var room = Interlocked.Exchange(ref _room, null);
            room?.Give(Bytes);
        }
    }
}
