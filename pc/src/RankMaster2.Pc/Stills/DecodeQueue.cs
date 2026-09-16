namespace RankMaster2.Pc.Stills;

/// <summary>
/// Two worker threads and the rank rule of plan section 3.5: visible ids (rank 0) may decode two
/// at once; a warm id (rank 1) starts only when the queue holds no rank-0 job and the other
/// worker is not currently running one. Duplicate jobs for one id are collapsed.
/// <para/>
/// Implemented as a locked list scanned for the best candidate each time a worker wants work,
/// rather than the <c>PriorityQueue&lt;TElement,TPriority&gt;</c> the plan names, because a
/// warm-pair eviction or a <c>ReleaseAsync</c> needs to pull a specific id back out of the queue
/// (<see cref="CancelQueued"/>) and the BCL priority queue has no removal. At the six-items-at-most
/// size this queue ever holds (plan section 3.3), a linear scan under the lock costs nothing the
/// plan's own numbers would notice; the scheduling behaviour described in section 3.5 is exact.
/// </summary>
internal sealed class DecodeQueue : IAsyncDisposable
{
    public const int VisibleRank = 0;
    public const int WarmRank = 1;

    private sealed class Job
    {
        public required string Folder;
        public required string Id;
        public required string Path;
        public required int PaneW;
        public required int PaneH;
        public required int Rank;
        public required long Seq;
        public required Action<string, string, DecodeResult> OnComplete;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly List<Job> _queue = [];
    private readonly Dictionary<string, Job> _byId = [];
    private readonly IStillDecoder _decoder;
    private readonly Thread[] _workers;
    private readonly int?[] _runningRank;
    private long _seq;
    private bool _stopping;

    public DecodeQueue(IStillDecoder decoder, int workerCount = 2)
    {
        _decoder = decoder;
        _runningRank = new int?[workerCount];
        _workers = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var slot = i;
            _workers[i] = new Thread(() => WorkerLoop(slot))
            {
                IsBackground = true,
                Name = $"RankMaster2.Stills.{slot}",
            };
            _workers[i].Start();
        }
    }

    /// <summary>For tests only: whether every worker thread has exited.</summary>
    internal bool AllWorkersStopped => _workers.All(w => !w.IsAlive);

    /// <summary>
    /// Queues a decode, or does nothing if one for this id is already queued or running (duplicate
    /// collapse). Returns a task that completes when the job is done, cancelled, or (for a
    /// duplicate) when the existing job finishes.
    /// </summary>
    public Task Enqueue(string folder, string id, string path, int paneW, int paneH, int rank, Action<string, string, DecodeResult> onComplete)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var existing))
                return existing.Done.Task;

            var job = new Job
            {
                Folder = folder,
                Id = id,
                Path = path,
                PaneW = paneW,
                PaneH = paneH,
                Rank = rank,
                Seq = _seq++,
                OnComplete = onComplete,
            };
            _byId[id] = job;
            _queue.Add(job);
            Monitor.PulseAll(_gate);
            return job.Done.Task;
        }
    }

    /// <summary>
    /// True removal for a job still sitting in the queue (never started -- its file is never
    /// opened). A no-op for a job already being decoded by a worker: that one cannot be
    /// interrupted (plan section 3.8), so its completion is what <see cref="InFlight"/> awaits.
    /// </summary>
    public void CancelQueued(string id)
    {
        lock (_gate)
        {
            var index = _queue.FindIndex(j => j.Id == id);
            if (index < 0) return;

            var job = _queue[index];
            _queue.RemoveAt(index);
            _byId.Remove(id);
            job.Done.TrySetResult();
        }
    }

    /// <summary>The completion task for id's job, whether queued or already running; null if nothing is tracked for id.</summary>
    public Task? InFlight(string id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var job) ? job.Done.Task : null;
        }
    }

    /// <summary>
    /// Plan section 3, ReleaseAllAsync: removes every job still sitting in the queue (they never
    /// start) and returns the completion tasks of whatever is left -- the jobs already running on
    /// a worker, which cannot be interrupted and must be waited for.
    /// </summary>
    public IReadOnlyList<Task> DrainAndCollectInFlight()
    {
        lock (_gate)
        {
            foreach (var job in _queue.ToList())
            {
                _queue.Remove(job);
                _byId.Remove(job.Id);
                job.Done.TrySetResult();
            }

            return _byId.Values.Select(j => (Task)j.Done.Task).ToList();
        }
    }

    private void WorkerLoop(int slot)
    {
        while (true)
        {
            Job? job;
            lock (_gate)
            {
                while (true)
                {
                    if (TryPickNext(slot, out job))
                        break;
                    if (_stopping)
                        return;
                    Monitor.Wait(_gate);
                }

                _runningRank[slot] = job!.Rank;
                _queue.Remove(job);
            }

            DecodeResult result;
            try
            {
                result = _decoder.Decode(job.Path, job.PaneW, job.PaneH);
            }
            catch (Exception ex)
            {
                // The decoder's own contract (plan section 3.7) already maps every decode failure
                // to a DecodeResult; this is a last-resort net so a worker thread can never die.
                result = DecodeResult.Fail(StillFailure.NotAnImage, $"{job.Id}: unexpected error ({ex.GetType().Name}: {ex.Message}).");
            }

            lock (_gate)
            {
                _runningRank[slot] = null;
                _byId.Remove(job.Id);
                Monitor.PulseAll(_gate);
            }

            try
            {
                job.OnComplete(job.Folder, job.Id, result);
            }
            finally
            {
                job.Done.TrySetResult();
            }
        }
    }

    /// <summary>Plan section 3.5's rule, exactly. Must be called with <see cref="_gate"/> held.</summary>
    private bool TryPickNext(int slot, out Job? job)
    {
        job = null;
        if (_queue.Count == 0)
            return false;

        Job? bestVisible = null;
        Job? bestWarm = null;
        foreach (var candidate in _queue)
        {
            if (candidate.Rank == VisibleRank)
            {
                if (bestVisible is null || candidate.Seq < bestVisible.Seq)
                    bestVisible = candidate;
            }
            else
            {
                if (bestWarm is null || candidate.Seq < bestWarm.Seq)
                    bestWarm = candidate;
            }
        }

        if (bestVisible is not null)
        {
            job = bestVisible;
            return true;
        }

        if (bestWarm is null)
            return false;

        // Warm ids decode one at a time, and only when no visible decode is queued or running
        // (plan section 3.5) -- so a warm pick is refused while ANY worker (this check runs before
        // this slot is marked busy, so "any" also covers "another") is running a visible decode,
        // and refused again while another worker is already running a warm one.
        foreach (var running in _runningRank)
        {
            if (running is VisibleRank or WarmRank)
                return false;
        }

        job = bestWarm;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        foreach (var worker in _workers)
            await Task.Run(worker.Join).ConfigureAwait(false);
    }
}
