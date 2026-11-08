namespace Bazaar.Server.Jobs;

/// <summary>
/// Claims due jobs and runs their handlers (doc 03, "Background jobs"). <see cref="PollOnceAsync"/> does one pass;
/// <see cref="RunAsync"/> polls on <see cref="JobPollerOptions.PollInterval"/> using the injected clock, so tests drive it
/// with a fake clock. Job due times and leases still use database time (see <see cref="JobStore"/>).
/// </summary>
public sealed class JobPoller
{
    private readonly JobStore _store;
    private readonly Dictionary<string, IJobHandler> _handlers;
    private readonly TimeProvider _time;

    public JobPoller(JobStore store, IEnumerable<IJobHandler> handlers, TimeProvider time, JobPollerOptions? options = null)
    {
        _store = store;
        _time = time;
        Options = options ?? new JobPollerOptions();
        Options.Validate();
        _handlers = new Dictionary<string, IJobHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Kind, handler))
                throw new ArgumentException($"More than one job handler is registered for kind '{handler.Kind}'.", nameof(handlers));
        }
    }

    /// <summary>The options this poller runs with.</summary>
    public JobPollerOptions Options { get; }

    /// <summary>This poller's lease owner. Each poller instance gets its own, so a restarted host never reclaims its old leases.</summary>
    public string Owner { get; } = Guid.CreateVersion7().ToString();

    /// <summary>The message from the last poll that failed outside a job (for example, the database was unreachable). Null after a good poll.</summary>
    public string? LastPollError { get; private set; }

    /// <summary>Runs one pass: fails exhausted leases, claims due jobs and runs each one in claim order. Returns the number claimed.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        await _store.FailExhaustedAsync(Options.MaxAttempts, ct).ConfigureAwait(false);
        var claimed = await _store.ClaimDueAsync(Owner, Options.LeaseDuration, Options.BatchSize, ct).ConfigureAwait(false);
        foreach (var job in claimed)
        {
            await RunOneAsync(job, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Polls until <paramref name="ct"/> is cancelled. Polls immediately, then once per interval. A failed poll is recorded in
    /// <see cref="LastPollError"/> and retried at the next tick; it does not stop the loop.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Options.PollInterval, _time);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                LastPollError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastPollError = ex.Message;
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RunOneAsync(JobRecord job, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(job.Kind, out var handler))
        {
            // Retried like any failure: another host may run a newer build that serves this kind.
            await FailAsync(job, $"no job handler is registered for kind '{job.Kind}'", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await handler.HandleAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(job, $"{ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        // A false return means the lease was lost while the handler ran; the job is then claimable again and will be retried.
        await _store.CompleteAsync(job.Id, Owner, ct).ConfigureAwait(false);
    }

    private Task<bool> FailAsync(JobRecord job, string error, CancellationToken ct) =>
        _store.FailAsync(job.Id, Owner, Options.MaxAttempts, Options.RetryDelayFor(job.Attempts), error, ct);
}
