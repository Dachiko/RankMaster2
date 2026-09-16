using RankMaster2.Pc.Stills;

namespace RankMaster2.Pc.Stills.Tests.Fixtures;

/// <summary>
/// An IStillDecoder that returns a frame of a given size after a controllable delay (plan section
/// 4), so StillSource's ordering, eviction and memory behaviour can be tested without Skia or real
/// image bytes. StillSource still stats real files on disk before deciding to decode (plan section
/// 1, "Stale bytes"), so tests using this fixture create real -- empty -- placeholder files for
/// every id, via <see cref="TempLibrary"/>.
/// </summary>
internal sealed class FakeDecoder : IStillDecoder
{
    private readonly object _gate = new();
    private readonly List<string> _log = [];
    private readonly Dictionary<string, int> _failuresRemaining = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public int FrameSize { get; set; } = 64;
    public DecodeBudget Budget { get; } = new(512L * 1024 * 1024);

    /// <summary>Ids passed to Decode, in call order.</summary>
    public IReadOnlyList<string> CallLog
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    /// <summary>The next `times` calls for id fail with NotAnImage instead of succeeding.</summary>
    public void FailNextDecode(string id, int times = 1)
    {
        lock (_gate) _failuresRemaining[id] = times;
    }

    public DecodeResult Decode(string path, int paneW, int paneH)
    {
        var id = Path.GetFileName(path);
        lock (_gate) _log.Add(id);

        if (Delay > TimeSpan.Zero)
            Thread.Sleep(Delay);

        lock (_gate)
        {
            if (_failuresRemaining.TryGetValue(id, out var remaining) && remaining > 0)
            {
                _failuresRemaining[id] = remaining - 1;
                return DecodeResult.Fail(StillFailure.NotAnImage, $"{id}: fake failure.");
            }
        }

        var buffer = Budget.Allocate((long)FrameSize * FrameSize * 4);
        var frame = new StillFrame(id, buffer, FrameSize, FrameSize, FrameSize, FrameSize, isPartial: false);
        return DecodeResult.Ok(frame);
    }
}

/// <summary>A temp folder with a real, empty, on-disk file for every id a FakeDecoder-based test uses.</summary>
internal sealed class TempLibrary : IDisposable
{
    public string Folder { get; } = Directory.CreateTempSubdirectory("rm2-stills-fake-").FullName;

    public string Touch(string id)
    {
        var path = Path.Combine(Folder, id);
        File.WriteAllBytes(path, []);
        return path;
    }

    public void TouchMany(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            Touch(id);
    }

    /// <summary>Overwrites id's file with different bytes and forces a distinct mtime, for staleness tests.</summary>
    public void Rewrite(string id, byte[] bytes)
    {
        var path = Path.Combine(Folder, id);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch { /* best effort */ }
    }
}
