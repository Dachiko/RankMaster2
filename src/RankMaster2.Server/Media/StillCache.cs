using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RankMaster2.Server.Media;

/// <summary>
/// The server's own disk cache for re-encoded stills.
/// <para/>
/// SERVER_SPEC.md § 12.5.6, in full: it "lives under the server's data directory and <b>never</b>
/// inside the user's media folder ... It is bounded by size with LRU eviction and is invisible to
/// the API: eviction never changes a response body, only its latency." Both halves are load-
/// bearing here. The cache root is checked against the session folder on every write, and a miss
/// is only ever a slower path to the same bytes, so a corrupt or deleted entry costs a re-render
/// and nothing else.
/// <para/>
/// The key is the ETag body — <c>variant</c> plus the 32 hex chars of the fingerprint — so an
/// entry is by construction the exact bytes that ETag promises, and a file replaced on disk lands
/// on a different key rather than overwriting the old one.
/// </summary>
public sealed class StillCache : IDisposable
{
    private sealed class Entry
    {
        public required string Path { get; init; }
        public required long Length { get; init; }
        public long LastAccessTicks;
    }

    private readonly MediaOptions _options;
    private readonly ILogger<StillCache> _log;
    private readonly string _root;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _renderLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evictionLock = new(1);
    private readonly object _lifecycleLock = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _scanTask;
    private long _totalBytes;
    private int _scanned;
    private int _activeOperations;
    private bool _disposing;

    public StillCache(IOptions<MediaOptions> options, ILogger<StillCache> log)
        : this(options.Value, log)
    {
    }

    public StillCache(MediaOptions options, ILogger<StillCache> log)
        : this(options, log, enumerateFiles: null)
    {
    }

    /// <summary>
    /// The test seam behind <see cref="IsScanned"/>: lets a test control the pace of the initial
    /// disk scan (A6) — a real one has no dial to turn a directory listing slow on demand.
    /// </summary>
    public StillCache(MediaOptions options, ILogger<StillCache> log, Func<string, IEnumerable<string>>? enumerateFiles)
    {
        _options = options;
        _log = log;
        _root = Path.GetFullPath(options.ResolvedCacheDirectory());
        _enumerateFiles = enumerateFiles ?? (root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));

        // A6: indexing a full, possibly-huge cache directory never runs on a request thread. It
        // starts here, once, on the thread pool; until it finishes, GetOrAddAsync bypasses the
        // cache rather than blocking on — or racing — the scan.
        _scanTask = Task.Run(ScanDirectory);
    }

    /// <summary>The directory the cache writes into. Never inside a media folder — see <see cref="IsInside"/>.</summary>
    public string Root => _root;

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public int Count => _entries.Count;

    /// <summary>True once the background scan started by the constructor has finished (A6).</summary>
    public bool IsScanned => Volatile.Read(ref _scanned) != 0;

    /// <summary>Completes when the background scan finishes — a seam for tests, never awaited on a request path.</summary>
    public Task WhenScanned => _scanTask;

    /// <summary>
    /// Returns the cached file for this key, touching its recency, or <see langword="null"/> on a
    /// miss. Quality is folded into the key: the ETag deliberately does not encode it (§ 12.3),
    /// so the only way a quality change cannot serve mismatched bytes is to miss instead.
    /// <para/>
    /// Before the background scan (A6) has finished this is always a miss — the index is not
    /// populated yet, and guessing at what a still-incomplete scan may already have added is
    /// worse than treating it as simply not there yet.
    /// </summary>
    public string? TryGet(string key)
    {
        if (!IsScanned)
            return null;

        if (!_entries.TryGetValue(key, out var entry))
            return null;

        if (!File.Exists(entry.Path))
        {
            Forget(key, entry);
            return null;
        }

        Interlocked.Exchange(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks);
        return entry.Path;
    }

    /// <summary>
    /// Produces the bytes for <paramref name="key"/> exactly once even under concurrent requests,
    /// writes them to the cache, and returns the file. <paramref name="render"/> receives the
    /// destination stream.
    /// <para/>
    /// <paramref name="mediaFolder"/> is the open session's folder: if the configured cache root
    /// is anywhere inside it the cache refuses to write, because scattering server files through
    /// the user's photos is the one thing § 12.5.6 forbids outright.
    /// </summary>
    public async Task<string?> GetOrAddAsync(
        string key,
        string extension,
        string mediaFolder,
        Func<Stream, Task> render,
        CancellationToken cancellationToken)
    {
        if (!TryEnterOperation())
            return null; // shutting down (K9): refuse rather than start work Dispose is already waiting to end.

        try
        {
            if (!IsScanned)
            {
                // A6: the background scan of the cache directory has not finished. Waiting for it
                // here would put a possibly-huge enumeration back on the request thread — exactly
                // what the scan was moved off of — and writing into a directory the scan is still
                // walking risks the scan re-discovering (or double-counting) what this write just
                // added. Served uncached, once, the same fallback already used below for a cache
                // root that cannot be written to at all.
                return null;
            }

            var hit = TryGet(key);
            if (hit is not null)
                return hit;

            if (IsInside(_root, mediaFolder))
            {
                _log.LogError(
                    "Still cache root {Root} is inside the session folder; refusing to write there (SERVER_SPEC.md § 12.5.6). Serving uncached.",
                    _root);
                return null;
            }

            var gate = _renderLocks.GetOrAdd(key, _ => new SemaphoreSlim(1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                hit = TryGet(key);
                if (hit is not null)
                    return hit;

                var path = PathFor(key, extension);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var temp = path + ".~" + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8];
                long length;
                try
                {
                    await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                    {
                        await render(stream).ConfigureAwait(false);
                        length = stream.Length;
                    }

                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }

                var entry = new Entry { Path = path, Length = length, LastAccessTicks = DateTime.UtcNow.Ticks };
                if (_entries.TryAdd(key, entry))
                    Interlocked.Add(ref _totalBytes, length);

                await EvictIfOverAsync().ConfigureAwait(false);
                return path;
            }
            finally
            {
                gate.Release();
                _renderLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// K9: <see cref="Dispose"/> must not dispose a per-key <see cref="SemaphoreSlim"/> while a
    /// render is still holding — or about to release — it. Every <see cref="GetOrAddAsync"/> call
    /// is counted as "in flight" for as long as it might touch <see cref="_renderLocks"/> or
    /// <see cref="_evictionLock"/>; <see cref="Dispose"/> stops new ones from entering and waits
    /// (bounded) for the ones already running before it disposes anything.
    /// </summary>
    private bool TryEnterOperation()
    {
        lock (_lifecycleLock)
        {
            if (_disposing)
                return false;

            _activeOperations++;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (_lifecycleLock)
        {
            _activeOperations--;
            if (_disposing && _activeOperations == 0)
                _drained.TrySetResult();
        }
    }

    /// <summary>
    /// Oldest access evicted first, until the cache is back inside its bound. Only latency
    /// changes; a body never does.
    /// </summary>
    private async Task EvictIfOverAsync()
    {
        if (TotalBytes <= _options.CacheMaxBytes)
            return;

        await _evictionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TotalBytes <= _options.CacheMaxBytes)
                return;

            var oldestFirst = _entries
                .ToArray()
                .OrderBy(pair => Interlocked.Read(ref pair.Value.LastAccessTicks))
                .ToArray();

            foreach (var (key, entry) in oldestFirst)
            {
                if (TotalBytes <= _options.CacheMaxBytes)
                    break;

                Forget(key, entry);
                TryDelete(entry.Path);
            }
        }
        finally
        {
            _evictionLock.Release();
        }
    }

    private void Forget(string key, Entry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
            Interlocked.Add(ref _totalBytes, -entry.Length);
    }

    /// <summary>
    /// Two hex chars of fan-out, so a big cache does not become one directory with a hundred
    /// thousand children.
    /// </summary>
    private string PathFor(string key, string extension) =>
        Path.Combine(_root, key[..2], key + "." + extension);

    /// <summary>
    /// Rebuilds the index from disk, so the cache survives a restart. A stray file is simply not
    /// indexed and will be overwritten or aged out.
    /// <para/>
    /// Runs once, off the thread pool, started by the constructor — never on a request thread
    /// (A6). <see cref="_scanned"/> is set in a <c>finally</c> so a scan that fails (an unreadable
    /// directory, a permissions error) still leaves the cache usable — empty, and writable — from
    /// then on, exactly as before this became asynchronous.
    /// </summary>
    private void ScanDirectory()
    {
        try
        {
            if (!Directory.Exists(_root))
                return;

            foreach (var file in _enumerateFiles(_root))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length == 0 || name.Contains('~'))
                {
                    TryDelete(file);
                    continue;
                }

                var info = new FileInfo(file);
                var entry = new Entry
                {
                    Path = file,
                    Length = info.Length,
                    LastAccessTicks = info.LastWriteTimeUtc.Ticks,
                };

                if (_entries.TryAdd(name, entry))
                    Interlocked.Add(ref _totalBytes, entry.Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not index the still cache at {Root}; starting empty.", _root);
        }
        finally
        {
            Volatile.Write(ref _scanned, 1);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A cache file that will not delete is a latency problem, never a correctness one.
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the folder itself or lies under it. Both sides
    /// are canonicalised first: the whole point is to catch a cache root reached through a
    /// symlink or a relative spelling.
    /// </summary>
    public static bool IsInside(string candidate, string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return false;

        var normalisedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var normalisedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return normalisedCandidate.Equals(normalisedFolder, comparison)
            || normalisedCandidate.StartsWith(normalisedFolder + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// K9: stops accepting new renders, waits (bounded) for whatever is already in flight, and
    /// only then disposes the per-key semaphores and the eviction lock. Disposing a semaphore a
    /// render is still waiting on — or about to release — throws <see cref="ObjectDisposedException"/>
    /// out of that request instead of letting it finish; the bound trades that, on shutdown only,
    /// for at most a few seconds' wait rather than hanging forever on a wedged render.
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _disposing = true;
            if (_activeOperations == 0)
                _drained.TrySetResult();
        }

        _drained.Task.Wait(TimeSpan.FromSeconds(5));

        _evictionLock.Dispose();
        foreach (var gate in _renderLocks.Values)
            gate.Dispose();
    }
}
