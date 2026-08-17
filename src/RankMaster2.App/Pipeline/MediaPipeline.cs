using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RankMaster2;

/// <summary>
/// Sequential media loader. Depth is <see cref="DefaultPrefetchPairs"/> (pairs ahead of the one on screen).
/// </summary>
public sealed class MediaPipeline : IMediaPipeline, IDisposable
{
    public const int DefaultPrefetchPairs = 2;

    private readonly BlockingCollection<MediaId> _work = new();
    private readonly ConcurrentDictionary<MediaId, byte> _queued = new();
    private readonly object _gate = new();
    private readonly Dictionary<MediaId, PreparedFrame> _cache = new();
    private readonly List<Pair> _warm = [];
    private readonly HashSet<MediaId> _wanted = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;

    private string? _folder;
    private int _panelW = 960;
    private int _panelH = 1080;
    private Pair? _visible;

    public MediaPipeline(int prefetchPairs = DefaultPrefetchPairs, Dispatcher? dispatcher = null)
    {
        if (prefetchPairs < 0)
            throw new ArgumentOutOfRangeException(nameof(prefetchPairs));
        PrefetchPairs = prefetchPairs;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var thread = new Thread(Worker) { IsBackground = true, Name = "RankMaster2.Pipeline" };
        thread.Start();
    }

    public int PrefetchPairs { get; }

    public event Action<MediaId, PreparedFrame>? Ready;
    public event Action<MediaId>? Failed;

    public void SetFolder(string folder) => _folder = folder;

    public void SetPanelPixelSize(int width, int height)
    {
        if (width < 16 || height < 16)
            return;
        _panelW = width;
        _panelH = height;
    }

    public void Show(MediaId left, MediaId right)
    {
        lock (_gate)
        {
            var pair = new Pair(left, right);
            _visible = pair;
            _warm.RemoveAll(p => p.Equals(pair));
            RebuildWanted();
            EvictUnwanted();
        }

        Request(left);
        Request(right);
        PublishCached(left);
        PublishCached(right);
    }

    public void Enqueue(Pair pair)
    {
        lock (_gate)
        {
            if (_warm.Count >= PrefetchPairs)
                return;
            if (_warm.Any(p => p.Equals(pair)))
                return;
            _warm.Add(pair);
            RebuildWanted();
        }

        Request(pair.Left);
        Request(pair.Right);
    }

    public void Release(MediaId id)
    {
        lock (_gate)
        {
            _cache.Remove(id);
            _wanted.Remove(id);
        }

        if (_folder is null)
            return;
        var path = Path.Combine(_folder, id.Filename);
        try
        {
            Catalog.FileOps.WaitUntilUnlocked(path);
        }
        catch (IOException)
        {
            // move retry will try again
        }
    }

    public void ReleaseAll()
    {
        List<MediaId> ids;
        lock (_gate)
        {
            ids = _wanted.Concat(_cache.Keys).Distinct().ToList();
            _cache.Clear();
            _wanted.Clear();
            _warm.Clear();
            _visible = null;
        }

        if (_folder is null)
            return;
        foreach (var id in ids)
        {
            try { Catalog.FileOps.WaitUntilUnlocked(Path.Combine(_folder, id.Filename)); }
            catch (IOException) { }
        }
    }

    public void CancelWarmContaining(MediaId id)
    {
        lock (_gate)
        {
            _warm.RemoveAll(p => p.Contains(id));
            RebuildWanted();
            EvictUnwanted();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _work.CompleteAdding();
        _cts.Dispose();
    }

    private void Request(MediaId id)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(id, out var frame) && !frame.IsPreview)
                return;
        }

        if (_queued.TryAdd(id, 0))
            _work.Add(id);
    }

    private void PublishCached(MediaId id)
    {
        PreparedFrame? frame;
        lock (_gate)
            _cache.TryGetValue(id, out frame);
        if (frame is not null)
            RaiseReady(id, frame);
    }

    private void Worker()
    {
        try
        {
            foreach (var id in _work.GetConsumingEnumerable(_cts.Token))
            {
                _queued.TryRemove(id, out _);
                if (!IsWanted(id))
                    continue;

                try
                {
                    LoadOne(id);
                }
                catch
                {
                    RaiseFailed(id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void LoadOne(MediaId id)
    {
        var folder = _folder;
        if (folder is null)
            return;

        var path = Path.Combine(folder, id.Filename);
        if (!File.Exists(path))
        {
            RaiseFailed(id);
            return;
        }

        var kind = MediaExtensions.KindOf(id.Filename);
        if (kind is null)
        {
            RaiseFailed(id);
            return;
        }

        if (kind == MediaKind.Video)
        {
            var frame = new PreparedFrame { Kind = MediaKind.Video, VideoPath = path };
            Store(id, frame);
            RaiseReady(id, frame);
            return;
        }

        var length = new FileInfo(path).Length;
        var panelW = _panelW;
        var panelH = _panelH;

        if (length > StillDecoder.PreviewIfLargerThanBytes)
        {
            var preview = StillDecoder.Decode(path, panelW, panelH, preview: true);
            if (!IsWanted(id))
                return;
            Store(id, preview);
            RaiseReady(id, preview);
        }

        if (!IsWanted(id))
            return;

        var full = StillDecoder.Decode(path, panelW, panelH, preview: false);
        if (!IsWanted(id))
            return;
        Store(id, full);
        RaiseReady(id, full);
    }

    private void Store(MediaId id, PreparedFrame frame)
    {
        lock (_gate)
        {
            if (_wanted.Contains(id))
                _cache[id] = frame;
        }
    }

    private bool IsWanted(MediaId id)
    {
        lock (_gate)
            return _wanted.Contains(id);
    }

    private void RebuildWanted()
    {
        _wanted.Clear();
        if (_visible is { } vis)
        {
            _wanted.Add(vis.Left);
            _wanted.Add(vis.Right);
        }
        foreach (var p in _warm)
        {
            _wanted.Add(p.Left);
            _wanted.Add(p.Right);
        }
    }

    private void EvictUnwanted()
    {
        var dead = _cache.Keys.Where(id => !_wanted.Contains(id)).ToList();
        foreach (var id in dead)
            _cache.Remove(id);
    }

    private void RaiseReady(MediaId id, PreparedFrame frame)
    {
        void Emit() => Ready?.Invoke(id, frame);
        if (_dispatcher.CheckAccess())
            Emit();
        else
            _dispatcher.BeginInvoke(Emit);
    }

    private void RaiseFailed(MediaId id)
    {
        void Emit() => Failed?.Invoke(id);
        if (_dispatcher.CheckAccess())
            Emit();
        else
            _dispatcher.BeginInvoke(Emit);
    }
}
