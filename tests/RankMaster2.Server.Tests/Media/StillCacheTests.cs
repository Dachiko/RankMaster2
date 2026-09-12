using Microsoft.Extensions.Logging.Abstractions;
using RankMaster2.Server.Media;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// SERVER_SPEC.md § 12.5.6: the still cache lives under the server's data directory and never
/// inside the user's media folder, is bounded by size with oldest-out eviction, and is invisible
/// to the API — "eviction never changes a response body, only its latency".
/// </summary>
public class StillCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rm2-cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _mediaFolder = Path.Combine(Path.GetTempPath(), "rm2-media-" + Guid.NewGuid().ToString("N")[..8]);

    public StillCacheTests() => Directory.CreateDirectory(_mediaFolder);

    private StillCache NewCache(long maxBytes = 1024 * 1024, string? root = null) =>
        new(new MediaOptions { CacheDirectory = root ?? _root, CacheMaxBytes = maxBytes }, NullLogger<StillCache>.Instance);

    private static Func<Stream, Task> Writes(byte[] payload) =>
        async stream => await stream.WriteAsync(payload);

    [Fact]
    public async Task FirstCall_Renders_SecondCallIsAHit()
    {
        using var cache = NewCache();
        var renders = 0;

        var payload = new byte[64];
        Task Render(Stream s)
        {
            Interlocked.Increment(ref renders);
            return s.WriteAsync(payload).AsTask();
        }

        var first = await cache.GetOrAddAsync("aa" + new string('0', 30), "jpg", _mediaFolder, Render, default);
        var second = await cache.GetOrAddAsync("aa" + new string('0', 30), "jpg", _mediaFolder, Render, default);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, renders);
        Assert.Equal(64, new FileInfo(first!).Length);
    }

    /// <summary>
    /// Two requests for the same key at the same time — the pair prefetch does this constantly —
    /// must decode once, not twice. Two concurrent decodes of the same 48 MP source is precisely
    /// the memory spike the layer is built to avoid.
    /// </summary>
    [Fact]
    public async Task ConcurrentRequestsForOneKey_RenderOnce()
    {
        using var cache = NewCache();
        var renders = 0;
        var gate = new SemaphoreSlim(0);

        async Task Render(Stream s)
        {
            Interlocked.Increment(ref renders);
            await gate.WaitAsync();
            await s.WriteAsync(new byte[32]);
        }

        var key = "bb" + new string('0', 30);
        var a = cache.GetOrAddAsync(key, "jpg", _mediaFolder, Render, default);
        var b = cache.GetOrAddAsync(key, "jpg", _mediaFolder, Render, default);

        gate.Release(2);
        var results = await Task.WhenAll(a, b);

        Assert.Equal(1, renders);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task CacheIsBounded_AndEvictsTheOldestAccessFirst()
    {
        using var cache = NewCache(maxBytes: 300);
        var payload = new byte[100];

        var keys = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var key = i.ToString("x2") + new string('0', 30);
            keys.Add(key);
            await cache.GetOrAddAsync(key, "jpg", _mediaFolder, Writes(payload), default);
            await Task.Delay(15); // distinct access times
        }

        Assert.Equal(3, cache.Count);

        // Touch the oldest so it is no longer the oldest access, then overflow the bound.
        Assert.NotNull(cache.TryGet(keys[0]));
        await Task.Delay(15);

        var overflow = "ff" + new string('0', 30);
        await cache.GetOrAddAsync(overflow, "jpg", _mediaFolder, Writes(payload), default);

        Assert.True(cache.TotalBytes <= 300, $"cache held {cache.TotalBytes} bytes, bound was 300");

        // keys[1] was the least recently used, so it is the one that went.
        Assert.Null(cache.TryGet(keys[1]));
        Assert.NotNull(cache.TryGet(keys[0]));
        Assert.NotNull(cache.TryGet(overflow));
    }

    /// <summary>
    /// Eviction is invisible: an evicted entry is simply re-rendered, byte for byte, on the next
    /// request. The API cannot tell the difference and neither can a client's ETag.
    /// </summary>
    [Fact]
    public async Task EvictionChangesLatency_NeverTheBytes()
    {
        using var cache = NewCache(maxBytes: 150);
        var payload = "the same bytes every time"u8.ToArray();

        var key = "cc" + new string('0', 30);
        var first = await cache.GetOrAddAsync(key, "jpg", _mediaFolder, Writes(payload), default);
        var firstBytes = await File.ReadAllBytesAsync(first!);

        for (var i = 0; i < 10; i++)
            await cache.GetOrAddAsync(i.ToString("x2") + new string('f', 30), "jpg", _mediaFolder, Writes(payload), default);

        var again = await cache.GetOrAddAsync(key, "jpg", _mediaFolder, Writes(payload), default);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(again!));
    }

    /// <summary>The index is rebuilt from disk, so a restart does not throw away a warm cache.</summary>
    [Fact]
    public async Task CacheSurvivesARestart()
    {
        var key = "dd" + new string('0', 30);
        var payload = new byte[128];

        using (var first = NewCache())
            await first.GetOrAddAsync(key, "jpg", _mediaFolder, Writes(payload), default);

        using var second = NewCache();
        var renders = 0;
        var hit = await second.GetOrAddAsync(key, "jpg", _mediaFolder, s =>
        {
            Interlocked.Increment(ref renders);
            return s.WriteAsync(payload).AsTask();
        }, default);

        Assert.NotNull(hit);
        Assert.Equal(0, renders);
        Assert.Equal(128, second.TotalBytes);
    }

    /// <summary>
    /// The rule § 12.5.6 states outright. A cache root configured inside the user's media folder
    /// is refused rather than obeyed: the request still gets correct bytes, uncached.
    /// </summary>
    [Fact]
    public async Task CacheRefusesToWriteInsideTheMediaFolder()
    {
        var insideMedia = Path.Combine(_mediaFolder, ".rm2cache");
        using var cache = NewCache(root: insideMedia);

        var result = await cache.GetOrAddAsync("ee" + new string('0', 30), "jpg", _mediaFolder, Writes(new byte[16]), default);

        Assert.Null(result);
        Assert.False(Directory.Exists(insideMedia));
        Assert.Empty(Directory.GetFileSystemEntries(_mediaFolder));
    }

    [Fact]
    public void IsInside_CatchesTheFolderItselfAndEverythingUnderIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "photos");

        Assert.True(StillCache.IsInside(folder, folder));
        Assert.True(StillCache.IsInside(Path.Combine(folder, "cache"), folder));
        Assert.True(StillCache.IsInside(Path.Combine(folder, "a", "b", "c"), folder));
        Assert.True(StillCache.IsInside(Path.Combine(folder, "x", "..", "y"), folder));

        Assert.False(StillCache.IsInside(Path.Combine(Path.GetTempPath(), "photos-cache"), folder));
        Assert.False(StillCache.IsInside(Path.GetTempPath(), folder));
        Assert.False(StillCache.IsInside(Path.Combine(folder, "..", "elsewhere"), folder));
    }

    [Fact]
    public async Task DefaultCacheDirectory_IsUnderTheServersOwnDataDirectory()
    {
        // Not in a media folder, not in the working directory, not beside the binary's inputs.
        var path = MediaOptions.DefaultCacheDirectory();
        Assert.Contains("RankMaster2", path);
        Assert.Contains("cache", path);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _mediaFolder })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A leftover temp directory is not a test failure.
            }
        }

        GC.SuppressFinalize(this);
    }
}
