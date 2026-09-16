namespace RankMaster2.Pc.Video;

/// <summary>A ring of the last <c>LogRingSize</c> LibVLC log lines (warning level and above — see
/// D-video.md § 5.2's one sanctioned option change if <c>--quiet</c> ever turns out to swallow them).
/// Attached to every <see cref="VideoFailure"/> as <c>Detail</c>, and to
/// <see cref="IVideoSurfaceFactory.DiagnosticsDump"/> in full, for the crash file and the owner's
/// report.</summary>
public sealed class VideoLog
{
    private readonly object _gate = new();
    private readonly string[] _ring;
    private int _next;
    private int _count;

    public VideoLog(int capacity = 100)
    {
        _ring = new string[Math.Max(1, capacity)];
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _ring[_next] = line;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length)
                _count++;
        }
    }

    /// <summary>The last <paramref name="count"/> lines, oldest first.</summary>
    public IReadOnlyList<string> Last(int count)
    {
        lock (_gate)
        {
            var take = Math.Min(count, _count);
            var result = new string[take];
            for (var i = 0; i < take; i++)
            {
                var idx = (_next - take + i + _ring.Length * 2) % _ring.Length;
                result[i] = _ring[idx];
            }

            return result;
        }
    }

    /// <summary>Every line still in the ring, oldest first.</summary>
    public string Dump()
    {
        lock (_gate)
            return string.Join('\n', Last(_ring.Length));
    }
}
