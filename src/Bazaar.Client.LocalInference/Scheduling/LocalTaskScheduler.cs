namespace Bazaar.Client.LocalInference.Scheduling;

/// <summary>
/// Runs local tasks on one backend, one execution at a time (doc 31, Design: Scheduling). Queued requests run by priority
/// (VoiceCleanup, UiAssist, PromptRewrite, ChipSummary), FIFO within a priority. Requests with the same kind and cache key
/// share one execution, and results are cached by (kind, cache key, model version). Background summaries are cancelled by
/// <see cref="CancelBackground"/> and wait while the power state reports battery saver. The scheduler does not own the backend.
/// </summary>
public sealed class LocalTaskScheduler : IAsyncDisposable
{
    private readonly ILocalModelBackend _backend;
    private readonly string _modelVersion;
    private readonly ILocalResultCache _cache;
    private readonly IPowerState _power;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly List<Job> _queue = [];
    private readonly Dictionary<string, Job> _inFlight = new(StringComparer.Ordinal);
    private Job? _running;
    private Task? _worker;
    private long _sequence;
    private bool _disposed;

    public LocalTaskScheduler(ILocalModelBackend backend, string modelVersion, ILocalResultCache cache, IPowerState? power = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrEmpty(modelVersion);
        ArgumentNullException.ThrowIfNull(cache);

        _backend = backend;
        _modelVersion = modelVersion;
        _cache = cache;
        _power = power ?? UnrestrictedPowerState.Instance;
    }

    /// <summary>
    /// Queues a request, or joins an identical one already queued or running. Cancelling <paramref name="ct"/> removes this
    /// caller; the execution itself is cancelled only when no caller is left waiting for it.
    /// </summary>
    public Task<LocalTaskResult> SubmitAsync(LocalTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<LocalTaskResult>(ct);
        }

        var key = ComposeKey(request);
        var waiter = new Waiter();
        Job job;
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.FromException<LocalTaskResult>(new ObjectDisposedException(nameof(LocalTaskScheduler)));
            }

            if (_cache.TryGet(key, out var cached))
            {
                return Task.FromResult(cached);
            }

            if (!_inFlight.TryGetValue(key, out job!))
            {
                job = new Job(key, request, _sequence++);
                _inFlight.Add(key, job);
                _queue.Add(job);
            }

            job.Waiters.Add(waiter);
            EnsureWorker();
        }

        waiter.Registration = ct.Register(() => Abandon(job, waiter, ct));
        return waiter.Completion;
    }

    /// <summary>Cancels queued and running <see cref="LocalTaskKind.ChipSummary"/> requests. User-driven kinds are untouched.</summary>
    public void CancelBackground()
    {
        var abandoned = new List<Waiter>();
        lock (_lock)
        {
            foreach (var job in _inFlight.Values.Where(job => job.Kind == LocalTaskKind.ChipSummary).ToList())
            {
                abandoned.AddRange(job.Waiters);
                job.Waiters.Clear();
                StopJob(job);
            }
        }

        Complete(abandoned, canceled: null);
    }

    /// <summary>Call when the platform power state changes, so that paused background work is re-checked.</summary>
    public void NotifyPowerStateChanged()
    {
        lock (_lock)
        {
            EnsureWorker();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var abandoned = new List<Waiter>();
        Task? worker;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var job in _queue)
            {
                abandoned.AddRange(job.Waiters);
                job.Waiters.Clear();
                _inFlight.Remove(job.Key);
                job.Cts.Cancel();
                job.Cts.Dispose();
            }

            _queue.Clear();
            _running?.Cts.Cancel();
            worker = _worker;
        }

        Complete(abandoned, canceled: null);
        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private string ComposeKey(LocalTaskRequest request) =>
        $"{_modelVersion.Length}:{_modelVersion}|{request.Kind}|{request.CacheKey}";

    private static int RankOf(LocalTaskKind kind) => kind switch
    {
        LocalTaskKind.VoiceCleanup => 0,
        LocalTaskKind.UiAssist => 1,
        LocalTaskKind.PromptRewrite => 2,
        LocalTaskKind.ChipSummary => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown task kind."),
    };

    // Requires _lock.
    private void EnsureWorker()
    {
        if (_worker is not null || _disposed || !_queue.Any(IsEligible))
        {
            return;
        }

        _worker = Task.Run(RunWorkerAsync);
    }

    // Requires _lock.
    private bool IsEligible(Job job) => job.Kind != LocalTaskKind.ChipSummary || !_power.IsBatterySaver;

    // Requires _lock.
    private Job? NextEligible()
    {
        Job? best = null;
        foreach (var job in _queue)
        {
            if (!IsEligible(job))
            {
                continue;
            }

            if (best is null
                || RankOf(job.Kind) < RankOf(best.Kind)
                || (RankOf(job.Kind) == RankOf(best.Kind) && job.Sequence < best.Sequence))
            {
                best = job;
            }
        }

        return best;
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (true)
            {
                Job job;
                CancellationToken token;
                lock (_lock)
                {
                    var next = NextEligible();
                    if (next is null)
                    {
                        return;
                    }

                    _queue.Remove(next);
                    _running = next;
                    job = next;
                    token = next.Cts.Token;
                }

                await ExecuteAsync(job, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _worker = null;
                EnsureWorker();
            }
        }
    }

    private async Task ExecuteAsync(Job job, CancellationToken token)
    {
        LocalTaskResult? result = null;
        Exception? error = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            result = await _backend.RunAsync(job.Request, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            _gate.Release();
        }

        lock (_lock)
        {
            _running = null;
        }

        Finish(job, result, error);
    }

    private void Finish(Job job, LocalTaskResult? result, Exception? error)
    {
        List<Waiter> waiters;
        lock (_lock)
        {
            RemoveInFlight(job);
            if (result is not null)
            {
                _cache.Store(job.Key, result);
            }

            waiters = [.. job.Waiters];
            job.Waiters.Clear();
            job.Cts.Dispose();
        }

        Complete(waiters, canceled: error as OperationCanceledException, result: result, error: error);
    }

    private void Abandon(Job job, Waiter waiter, CancellationToken token)
    {
        lock (_lock)
        {
            if (!job.Waiters.Remove(waiter))
            {
                return;
            }

            if (job.Waiters.Count == 0)
            {
                StopJob(job);
            }
        }

        waiter.Registration.Dispose();
        waiter.Tcs.TrySetCanceled(token);
    }

    // Requires _lock.
    private void StopJob(Job job)
    {
        RemoveInFlight(job);
        job.Cts.Cancel();
        if (_queue.Remove(job))
        {
            job.Cts.Dispose();
        }
    }

    // Requires _lock.
    private void RemoveInFlight(Job job)
    {
        if (_inFlight.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
        {
            _inFlight.Remove(job.Key);
        }
    }

    /// <summary>Completes waiters outside <see cref="_lock"/>, since disposing a registration waits for a running callback.</summary>
    private static void Complete(
        List<Waiter> waiters,
        OperationCanceledException? canceled,
        LocalTaskResult? result = null,
        Exception? error = null)
    {
        foreach (var waiter in waiters)
        {
            waiter.Registration.Dispose();
            if (result is not null)
            {
                waiter.Tcs.TrySetResult(result);
            }
            else if (canceled is not null)
            {
                waiter.Tcs.TrySetCanceled(canceled.CancellationToken);
            }
            else if (error is not null)
            {
                waiter.Tcs.TrySetException(error);
            }
            else
            {
                waiter.Tcs.TrySetCanceled();
            }
        }
    }

    private sealed class Job(string key, LocalTaskRequest request, long sequence)
    {
        public string Key { get; } = key;

        public LocalTaskRequest Request { get; } = request;

        public LocalTaskKind Kind => Request.Kind;

        public long Sequence { get; } = sequence;

        public List<Waiter> Waiters { get; } = [];

        public CancellationTokenSource Cts { get; } = new();
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<LocalTaskResult> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }

        public Task<LocalTaskResult> Completion => Tcs.Task;
    }
}
