namespace Bazaar.Kodo.Probes;

/// <summary>
/// A bounded pool for probes (doc 46 section 7.3). A probe that cannot get a slot right away is not queued:
/// it yields <c>unknown</c> with cause <c>bulkhead_full</c>, so a saturated pool degrades only its own probes.
/// A probe that runs past its timeout is cancelled, reported as <c>timeout</c>, and its slot is released.
/// </summary>
public sealed class Bulkhead : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _timeProvider;

    public Bulkhead(string pool, int maxConcurrency, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pool);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        Pool = pool;
        MaxConcurrency = maxConcurrency;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The pool name from doc 46 section 7.3: <c>local</c>, <c>db</c>, <c>external</c> or <c>slow</c>.</summary>
    public string Pool { get; }

    public int MaxConcurrency { get; }

    /// <summary>Releases the pool's semaphore. Call it only after every probe in the pool has finished.</summary>
    public void Dispose() => _slots.Dispose();

    /// <summary>Free slots right now. Read for status only; it can change before the caller acts on it.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>
    /// Runs <paramref name="probe"/> in a slot with a hard timeout. Probe failures never reach the caller:
    /// an exception becomes red with cause <c>probe_exception</c>, and a timeout becomes unknown with cause
    /// <c>timeout</c>. Only the caller's own cancellation is thrown, because that is a shutdown, not a probe result.
    /// </summary>
    public async Task<ProbeResult> RunAsync(
        Func<CancellationToken, Task<ProbeResult>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        // Non-blocking acquire: a zero wait takes a free slot or reports saturation at once.
        if (!await _slots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return ProbeResult.Unknown(ProbeCauses.BulkheadFull);
        }

        // The budget token stops the probe when the timeout fires or the caller cancels. It is always
        // cancelled in finally, which also stops the timer from the Task.Delay below.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Task<ProbeResult> work = RunProbeAsync(probe, budget.Token);
            Task deadline = Task.Delay(timeout, _timeProvider, budget.Token);
            Task finished = await Task.WhenAny(work, deadline).ConfigureAwait(false);
            if (finished == work)
            {
                return await work.ConfigureAwait(false);
            }

            // The deadline fired, or the caller cancelled. Caller cancellation is not a probe result.
            cancellationToken.ThrowIfCancellationRequested();
            return ProbeResult.Timeout();
        }
        finally
        {
            budget.Cancel();
            _slots.Release();
        }
    }

    // Turns every exception from the probe into a result. A cancelled budget means the timeout (or the caller)
    // stopped the probe, so the late outcome is dropped: the caller has already recorded the timeout.
    private static async Task<ProbeResult> RunProbeAsync(Func<CancellationToken, Task<ProbeResult>> probe, CancellationToken token)
    {
        try
        {
            return await probe(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return ProbeResult.Timeout();
        }
        catch (Exception error)
        {
            return ProbeResult.FromException(error);
        }
    }
}
