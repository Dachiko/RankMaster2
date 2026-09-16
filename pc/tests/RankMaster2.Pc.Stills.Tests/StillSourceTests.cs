using System.Diagnostics;
using RankMaster2.Pc.Stills;
using RankMaster2.Pc.Stills.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// StillSource's ordering, eviction, memory and release behaviour, against FakeDecoder (plan
/// section 6). StillSource still stats real files (plan section 1, "Stale bytes"), so every test
/// uses a TempLibrary -- a real, empty, on-disk placeholder file per id.
/// </summary>
public class StillSourceTests(ITestOutputHelper output)
{
    private sealed class ChangedRecorder
    {
        public readonly record struct Event(string Id, StillState State, int ThreadId);

        private readonly List<Event> _events = [];
        private readonly object _gate = new();

        public void Attach(StillSource source) => source.Changed += OnChanged;

        private void OnChanged(string id, StillState state)
        {
            lock (_gate) _events.Add(new Event(id, state, Environment.CurrentManagedThreadId));
            // E's contract: dispose the lease once its bitmap copy is made (plan section 2.1).
            if (state is StillState.Ready ready) ready.Lease.Dispose();
        }

        public IReadOnlyList<Event> Events { get { lock (_gate) return _events.ToArray(); } }
        public IReadOnlyList<Event> For(string id) => Events.Where(e => e.Id == id).ToArray();
    }

    private static bool DisposeIfReady(StillState state)
    {
        if (state is StillState.Ready r) { r.Lease.Dispose(); return true; }
        return false;
    }

    private static bool AllReady(StillSource source, params string[] ids)
    {
        var allReady = true;
        foreach (var id in ids)
            if (!DisposeIfReady(source.StateOf(id)))
                allReady = false;
        return allReady;
    }

    private static StillLease GetReadyLease(StillSource source, string id)
    {
        var state = source.StateOf(id);
        return state is StillState.Ready r ? r.Lease : throw new InvalidOperationException($"{id} is not Ready: {state}");
    }

    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(5);
        }
        return predicate();
    }

    [Fact]
    public async Task Show_raises_Pending_then_Ready_for_both_ids()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(100) };
        await using var source = new StillSource(decoder);
        var recorder = new ChangedRecorder();
        recorder.Attach(source);

        var mainThread = Environment.CurrentManagedThreadId;
        source.Show(lib.Folder, "a", "b");

        Assert.True(await WaitUntil(() => recorder.For("a").Any(e => e.State is StillState.Ready), TimeSpan.FromSeconds(2)));
        Assert.True(await WaitUntil(() => recorder.For("b").Any(e => e.State is StillState.Ready), TimeSpan.FromSeconds(2)));

        foreach (var id in new[] { "a", "b" })
        {
            var events = recorder.For(id);
            Assert.True(events.Count >= 2, $"{id}: expected at least Pending then Ready, got {events.Count} events");
            Assert.IsType<StillState.Pending>(events[0].State);
            Assert.Equal(mainThread, events[0].ThreadId);
            Assert.IsType<StillState.Ready>(events[^1].State);
            Assert.NotEqual(mainThread, events[^1].ThreadId);
        }
    }

    [Fact]
    public async Task Show_of_a_warm_pair_is_Ready_synchronously()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["c", "d"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(50) };
        await using var source = new StillSource(decoder);

        source.Warm(lib.Folder, [("c", "d")]);
        Assert.True(await WaitUntil(() => AllReady(source, "c", "d"), TimeSpan.FromSeconds(2)));
        Assert.Equal(2, decoder.CallLog.Count(x => x is "c" or "d"));

        var recorder = new ChangedRecorder();
        recorder.Attach(source);
        source.Show(lib.Folder, "c", "d");

        Assert.Single(recorder.For("c"));
        Assert.Single(recorder.For("d"));
        Assert.IsType<StillState.Ready>(recorder.For("c")[0].State);
        Assert.IsType<StillState.Ready>(recorder.For("d")[0].State);
        Assert.Equal(2, decoder.CallLog.Count(x => x is "c" or "d"));
    }

    [Fact]
    public async Task Warm_never_runs_while_a_visible_decode_is_pending()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b", "c", "d", "e", "f"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(150) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        source.Warm(lib.Folder, [("c", "d"), ("e", "f")]);

        Assert.True(await WaitUntil(() => decoder.CallLog.Count(x => x is "c" or "d" or "e" or "f") == 4, TimeSpan.FromSeconds(5)));

        var log = decoder.CallLog;
        output.WriteLine($"call log: {string.Join(", ", log)}");
        var aIndex = log.ToList().IndexOf("a");
        var bIndex = log.ToList().IndexOf("b");
        var warmIndexes = log.Select((x, i) => (x, i)).Where(t => t.x is "c" or "d" or "e" or "f").Select(t => t.i).ToArray();

        Assert.True(aIndex < warmIndexes.Min());
        Assert.True(bIndex < warmIndexes.Min());
        Assert.Equal([2, 3, 4, 5], warmIndexes); // c,d,e,f strictly one after another, nothing interleaved
        Assert.Equal(["c", "d", "e", "f"], log.Skip(2));

        DisposeIfReady(source.StateOf("a"));
        DisposeIfReady(source.StateOf("b"));
    }

    [Fact]
    public async Task Show_preempts_warm()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b", "c", "d", "g", "h"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(200) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => decoder.CallLog.Count(x => x is "a" or "b") == 2, TimeSpan.FromSeconds(2)));
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));

        source.Warm(lib.Folder, [("c", "d")]);
        Assert.True(await WaitUntil(() => decoder.CallLog.Contains("c"), TimeSpan.FromSeconds(2)));

        source.Show(lib.Folder, "g", "h"); // preempts while c is mid-decode; d has not started

        Assert.True(await WaitUntil(() => decoder.CallLog.Count(x => x is "g" or "h") == 2, TimeSpan.FromSeconds(3)));

        var log = decoder.CallLog.ToList();
        var gIndex = log.IndexOf("g");
        var hIndex = log.IndexOf("h");
        var dIndex = log.IndexOf("d");
        output.WriteLine($"call log: {string.Join(", ", log)}");
        Assert.True(dIndex == -1 || (gIndex < dIndex && hIndex < dIndex), $"expected g,h before d; log = {string.Join(",", log)}");

        DisposeIfReady(source.StateOf("g"));
        DisposeIfReady(source.StateOf("h"));
    }

    [Fact]
    public async Task Visible_ids_decode_two_at_once()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(150) };
        await using var source = new StillSource(decoder);

        var sw = Stopwatch.StartNew();
        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => decoder.CallLog.Count(x => x is "a" or "b") == 2, TimeSpan.FromSeconds(2)));
        sw.Stop();

        output.WriteLine($"both decodes dispatched within {sw.ElapsedMilliseconds} ms (decode itself takes 150 ms)");
        Assert.True(sw.ElapsedMilliseconds < 100, $"expected both to start within ~20 ms of each other, dispatch took {sw.ElapsedMilliseconds} ms");

        DisposeIfReady(source.StateOf("a"));
        DisposeIfReady(source.StateOf("b"));
    }

    [Fact]
    public async Task Duplicate_requests_collapse()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(150) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        source.Show(lib.Folder, "a", "b");

        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));
        await Task.Delay(200);

        Assert.Equal(1, decoder.CallLog.Count(x => x == "a"));
        Assert.Equal(1, decoder.CallLog.Count(x => x == "b"));
    }

    [Fact]
    public async Task Eviction_is_exactly_the_wanted_set()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b", "c", "d", "e", "f", "g", "h"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(30) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        source.Warm(lib.Folder, [("c", "d"), ("e", "f")]);
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b", "c", "d", "e", "f"), TimeSpan.FromSeconds(3)));

        var aLease = GetReadyLease(source, "a");
        var bLease = GetReadyLease(source, "b");
        _ = aLease.Frame.Pixels;
        _ = bLease.Frame.Pixels;

        source.Show(lib.Folder, "c", "d");
        source.Warm(lib.Folder, [("e", "f"), ("g", "h")]);

        // Eviction of a,b happened synchronously above. The cache's own reference is gone, but our
        // held leases keep the buffer valid until we dispose them (plan section 3.6).
        _ = aLease.Frame.Pixels;
        _ = bLease.Frame.Pixels;

        aLease.Dispose();
        bLease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => aLease.Frame.Pixels);
        Assert.Throws<ObjectDisposedException>(() => bLease.Frame.Pixels);

        Assert.True(await WaitUntil(() => AllReady(source, "c", "d", "e", "f", "g", "h"), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Extra_warm_pairs_beyond_prefetchPairs_are_ignored()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b", "c", "d", "e", "f"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(50) };
        await using var source = new StillSource(decoder);

        source.Warm(lib.Folder, [("a", "b"), ("c", "d"), ("e", "f")], prefetchPairs: 2);
        await Task.Delay(500);

        Assert.DoesNotContain("e", decoder.CallLog);
        Assert.DoesNotContain("f", decoder.CallLog);

        DisposeIfReady(source.StateOf("a"));
        DisposeIfReady(source.StateOf("b"));
        DisposeIfReady(source.StateOf("c"));
        DisposeIfReady(source.StateOf("d"));
    }

    [Fact]
    public async Task Warm_raises_no_Changed()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(50) };
        await using var source = new StillSource(decoder);
        var recorder = new ChangedRecorder();
        recorder.Attach(source);

        source.Warm(lib.Folder, [("a", "b")]);
        await Task.Delay(300);

        Assert.Empty(recorder.Events);
        DisposeIfReady(source.StateOf("a"));
        DisposeIfReady(source.StateOf("b"));
    }

    [Fact]
    public async Task ResidentMemoryIsBoundedByTheWantedSet()
    {
        const int idCount = 20_000;
        const int cycles = 2_000;
        var ids = new string[idCount];
        for (var i = 0; i < idCount; i++) ids[i] = $"id_{i:D6}.jpg";

        using var lib = new TempLibrary();
        lib.TouchMany(ids);

        const int frameSize = 1024; // 1024*1024*4 bytes per frame
        var frameBytes = (long)frameSize * frameSize * 4;
        var decoder = new FakeDecoder { Delay = TimeSpan.Zero, FrameSize = frameSize };
        await using var source = new StillSource(decoder);

        var recorder = new ChangedRecorder(); // disposes every Ready lease immediately
        recorder.Attach(source);

        var rng = new Random(12345);
        for (var i = 0; i < cycles; i++)
        {
            var left = ids[rng.Next(idCount)];
            var right = ids[rng.Next(idCount)];
            source.Show(lib.Folder, left, right);

            var warmPairs = new List<(string, string)>
            {
                (ids[rng.Next(idCount)], ids[rng.Next(idCount)]),
                (ids[rng.Next(idCount)], ids[rng.Next(idCount)]),
            };
            source.Warm(lib.Folder, warmPairs);
        }

        Assert.True(await WaitUntil(() => decoder.Budget.LiveBytes <= 6 * frameBytes, TimeSpan.FromSeconds(60)));
        await Task.Delay(300);

        output.WriteLine($"LiveBytes={decoder.Budget.LiveBytes / 1024.0 / 1024.0:F1} MB, PeakBytes={decoder.Budget.PeakBytes / 1024.0 / 1024.0:F1} MB, 6 frames={6 * frameBytes / 1024.0 / 1024.0:F1} MB, 8 frames={8 * frameBytes / 1024.0 / 1024.0:F1} MB");

        Assert.True(decoder.Budget.LiveBytes <= 6 * frameBytes, $"LiveBytes {decoder.Budget.LiveBytes} exceeds 6 frames ({6 * frameBytes})");
        Assert.True(decoder.Budget.PeakBytes <= 8 * frameBytes, $"PeakBytes {decoder.Budget.PeakBytes} exceeds 8 frames ({8 * frameBytes})");
    }

    [Fact]
    public async Task A_stale_file_is_redecoded()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(30) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));
        Assert.Equal(1, decoder.CallLog.Count(x => x == "a"));

        lib.Rewrite("a", [1, 2, 3, 4, 5]);

        var recorder = new ChangedRecorder();
        recorder.Attach(source);
        source.Show(lib.Folder, "a", "b");

        Assert.IsType<StillState.Pending>(recorder.For("a").First().State);
        Assert.True(await WaitUntil(() => recorder.For("a").Any(e => e.State is StillState.Ready), TimeSpan.FromSeconds(2)));

        Assert.Equal(2, decoder.CallLog.Count(x => x == "a"));
        Assert.Single(recorder.For("b"));
        Assert.IsType<StillState.Ready>(recorder.For("b")[0].State);
        Assert.Equal(1, decoder.CallLog.Count(x => x == "b"));
    }

    [Fact]
    public async Task ReleaseAsync_waits_for_the_inflight_decode()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(300) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        await Task.Delay(50);

        var sw = Stopwatch.StartNew();
        var released = await source.ReleaseAsync(lib.Folder, "a", CancellationToken.None);
        sw.Stop();

        output.WriteLine($"ReleaseAsync returned after {sw.ElapsedMilliseconds} ms");
        Assert.True(released);
        Assert.True(sw.ElapsedMilliseconds >= 200, $"expected to wait for the in-flight decode (~250 ms remaining), returned after {sw.ElapsedMilliseconds} ms");

        Assert.IsType<StillState.Pending>(source.StateOf("a"));
        DisposeIfReady(source.StateOf("b"));
    }

    [Fact]
    public async Task ReleaseAsync_on_a_locked_file_returns_false()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder();
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));

        var path = Path.Combine(lib.Folder, "a");
        await using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var released = await source.ReleaseAsync(lib.Folder, "a", CancellationToken.None);

        // The plan expected Linux not to enforce sharing, so that this assertion could only be
        // Windows-only fact. Measured here: .NET's Unix FileStream DOES honour FileShare.None
        // against a second FileStream opened by the SAME PROCESS (it emulates it with flock()),
        // so this returns false here too, for a same-process lock. What is genuinely unverifiable
        // on this box is a lock held by a DIFFERENT (non-.NET, or cross-process) program, which is
        // the actual scenario section 3.8 exists for on Windows.
        output.WriteLine(OperatingSystem.IsWindows()
            ? "Windows: cross-process sharing is enforced by the OS."
            : "Linux: a same-process FileShare.None conflict is still refused (.NET emulates it via flock()); a lock held by another program is not exercised by this test.");
        Assert.False(released);
    }

    [Fact]
    public async Task ReleaseAllAsync_then_Show_works()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(20) };
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));

        await source.ReleaseAllAsync(CancellationToken.None);
        Assert.Equal(0, decoder.Budget.LiveBytes);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Failed_state_is_cached_and_cleared_by_Release()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(20) };
        decoder.FailNextDecode("a");
        await using var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => source.StateOf("a") is StillState.Failed, TimeSpan.FromSeconds(2)));
        DisposeIfReady(source.StateOf("b"));
        Assert.Equal(1, decoder.CallLog.Count(x => x == "a"));

        source.Show(lib.Folder, "a", "b"); // still failed, cached; no redecode
        DisposeIfReady(source.StateOf("b"));
        await Task.Delay(100);
        Assert.Equal(1, decoder.CallLog.Count(x => x == "a"));

        await source.ReleaseAsync(lib.Folder, "a", CancellationToken.None);

        source.Show(lib.Folder, "a", "b"); // decoder called again
        Assert.True(await WaitUntil(() => AllReady(source, "a"), TimeSpan.FromSeconds(2)));
        DisposeIfReady(source.StateOf("b"));
        Assert.Equal(2, decoder.CallLog.Count(x => x == "a"));
    }

    [Fact]
    public async Task DisposeAsync_stops_the_workers_and_frees_everything()
    {
        using var lib = new TempLibrary();
        lib.TouchMany(["a", "b"]);
        var decoder = new FakeDecoder { Delay = TimeSpan.FromMilliseconds(20) };
        var source = new StillSource(decoder);

        source.Show(lib.Folder, "a", "b");
        Assert.True(await WaitUntil(() => AllReady(source, "a", "b"), TimeSpan.FromSeconds(2)));

        await source.DisposeAsync();

        Assert.Equal(0, decoder.Budget.LiveBytes);
        Assert.True(source.DebugQueue.AllWorkersStopped);
    }
}
