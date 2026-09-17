using System.Diagnostics;

namespace RankMaster2.Server.Media;

/// <summary>
/// AUDIT2.md § 2.1: <see cref="MediaOptions.MaxConcurrentDecodes"/> admits two decodes at once
/// against one shared <see cref="DecodeBudget"/>, with no coordination between them. Two large
/// stills — one pair, the product's whole unit of work — collide there: the first is admitted, the
/// second cannot fit and <see cref="DecodeBudget.Allocate"/> throws, which <c>StillRenderer</c>
/// reported as <c>422 media_decode_failed</c> — "could not be decoded" — for a file that was never
/// the problem. Measured at 12 refusals out of 12 pairs.
/// <para/>
/// This is the fix, ported verbatim in spirit from the PC client's identical defect and its fix
/// (<c>pc/src/RankMaster2.Pc/Stills/DecodeRoom.cs</c>), which documents the trap this class exists
/// to avoid:
/// <para/>
/// <b>Reservations, not retries.</b> A decode reserves its whole worst-case peak here BEFORE it
/// allocates anything, and holds the reservation until it is finished. That is what makes waiting
/// safe: a waiter holds no memory, so two decodes can never sit waiting for each other's buffers. A
/// plain "allocate, and wait if it does not fit" would deadlock exactly there — each decode holding
/// its decode buffer and waiting for room to grow. <c>StillRenderer</c> reserves pixel buffers in
/// several places inside one render (the decode buffer, then a resample buffer, then an orientation
/// buffer); only the FIRST reservation — taken before any of those exist — is allowed to wait here.
/// The later, in-render allocations go straight to <see cref="DecodeBudget.Allocate"/>, which never
/// waits, precisely so a render already holding memory can never block on another render that is in
/// turn waiting on this one.
/// <para/>
/// <b>What it costs.</b> Admission compares <c>LiveBytes + outstanding reservations + this one</c>
/// with the ceiling, and <c>LiveBytes</c> already includes whatever the outstanding reservations
/// have actually spent, so the test is conservative: two decodes whose peaks would in fact have
/// fitted side by side can be made to run one after the other instead of concurrently. Slower, never
/// wrong — and never the thing this exists to prevent, which is a real photograph reported as a file
/// that will not decode. Ordinary photographs, which are nowhere near the ceiling, are unaffected:
/// they are admitted immediately, exactly as before.
/// <para/>
/// A request larger than the whole ceiling is refused at once and does not wait: no amount of
/// waiting makes room that does not exist. That is the deliberate, documented ceiling
/// (<see cref="MediaOptions.DecodeMemoryLimitMegabytes"/>), and it must still reach the caller as the
/// same <c>422 media_decode_failed</c> it always has — with a message that says "too big", not
/// "damaged".
/// </summary>
public sealed class DecodeRoom
{
    /// <summary>Backstop only. A reservation released by another decode pulses the wait immediately,
    /// so this expires only if a decode is stuck for far longer than any decode takes; the honest
    /// answer then is the same one a single oversized file gets — refused, not hung forever.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();

    /// <summary>The budget this room admits decodes into.</summary>
    public DecodeBudget Budget { get; }

    private readonly TimeSpan _maxWait;
    private readonly TimeSpan _poll;
    private long _reserved;
    private TimeSpan _lastWait;
    private int _waited;

    public DecodeRoom(DecodeBudget budget, TimeSpan? maxWait = null, TimeSpan? poll = null)
    {
        Budget = budget;
        _maxWait = maxWait ?? DefaultMaxWait;
        // A frame freed elsewhere lowers LiveBytes without passing through this class, so a waiter
        // also re-checks on a timer rather than relying only on a pulse.
        _poll = poll ?? TimeSpan.FromMilliseconds(20);
    }

    /// <summary>Bytes currently promised to decodes that have not finished.</summary>
    public long ReservedBytes { get { lock (_gate) return _reserved; } }

    /// <summary>How long the last <see cref="Take"/> spent waiting for room.</summary>
    public TimeSpan LastWait { get { lock (_gate) return _lastWait; } }

    /// <summary>How many decodes have had to wait for another to finish. Monotonic, so it is the
    /// honest answer to "was this pair serialised rather than refused?" — which is the whole of
    /// AUDIT2.md § 2.1's remedy.</summary>
    public int WaitedAdmissions { get { lock (_gate) return _waited; } }

    /// <summary>
    /// Reserves <paramref name="peakBytes"/> of headroom, waiting for another decode to finish if
    /// that is what it takes. Throws <see cref="DecodeBudgetExceededException"/> only when the
    /// request cannot fit inside an empty budget, or when the wait ran out.
    /// </summary>
    public Reservation Take(long peakBytes)
    {
        if (peakBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(peakBytes), peakBytes, "A decode reserves a positive number of bytes.");

        if (peakBytes > Budget.CeilingBytes)
            throw new DecodeBudgetExceededException(peakBytes, 0, Budget.CeilingBytes);

        var clock = Stopwatch.StartNew();
        var waited = false;
        lock (_gate)
        {
            while (true)
            {
                var live = Budget.LiveBytes;
                if (live + _reserved + peakBytes <= Budget.CeilingBytes)
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
                    throw new DecodeBudgetExceededException(peakBytes, live + _reserved, Budget.CeilingBytes);
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
    public sealed class Reservation : IDisposable
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
