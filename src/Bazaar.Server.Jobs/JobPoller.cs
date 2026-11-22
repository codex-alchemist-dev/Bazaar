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
    private readonly RecurringJobScheduler? _recurring;

    public JobPoller(
        JobStore store,
        IEnumerable<IJobHandler> handlers,
        TimeProvider time,
        JobPollerOptions? options = null,
        RecurringJobScheduler? recurring = null)
    {
        _store = store;
        _time = time;
        _recurring = recurring;
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

    /// <summary>
    /// Runs one pass: fails exhausted leases, then claims and runs due jobs one at a time, up to <see cref="JobPollerOptions.BatchSize"/>.
    /// Jobs run one after another, so each is claimed only when it can start: a lease never ticks away while earlier jobs run.
    /// Returns the number claimed.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        if (_recurring is not null)
            await _recurring.EnqueueDueAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);

        await _store.FailExhaustedAsync(Options.MaxAttempts, ct).ConfigureAwait(false);
        // A job that failed and is due again at once is left for the next poll, as when the whole batch was claimed up front.
        var ran = new List<long>();
        while (ran.Count < Options.BatchSize)
        {
            var claimed = await _store.ClaimDueAsync(Owner, Options.LeaseDuration, 1, ran, ct).ConfigureAwait(false);
            if (claimed.Count == 0)
                break;
            ran.Add(claimed[0].Id);
            await RunOneAsync(claimed[0], ct).ConfigureAwait(false);
        }

        return ran.Count;
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

        // The lease is renewed while the handler runs, so a long job is not reclaimed and run a second time. If a renewal
        // finds the lease gone, the handler's token is cancelled: another poller owns the job now.
        using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewStop = new CancellationTokenSource();
        var renewal = RenewLeaseAsync(job.Id, handlerCts, renewStop.Token);
        try
        {
            await handler.HandleAsync(job, handlerCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await StopRenewalAsync(renewal, renewStop).ConfigureAwait(false);
            await FailAsync(job, $"{ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }
        catch
        {
            await StopRenewalAsync(renewal, renewStop).ConfigureAwait(false);
            throw;
        }

        await StopRenewalAsync(renewal, renewStop).ConfigureAwait(false);

        // A false return means the lease was lost while the handler ran; the job is then claimable again and will be retried.
        await _store.CompleteAsync(job.Id, Owner, ct).ConfigureAwait(false);
    }

    private static async Task StopRenewalAsync(Task renewal, CancellationTokenSource renewStop)
    {
        await renewStop.CancelAsync().ConfigureAwait(false);
        await renewal.ConfigureAwait(false);
    }

    /// <summary>Extends the lease every third of the lease duration until stopped. A failed renewal is retried at the next tick.</summary>
    private async Task RenewLeaseAsync(long id, CancellationTokenSource handlerCts, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Options.LeaseDuration / 3, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    if (!await _store.ExtendLeaseAsync(id, Owner, Options.LeaseDuration, stop).ConfigureAwait(false))
                    {
                        await handlerCts.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LastPollError = $"lease renewal for job {id} failed: {ex.Message}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The handler finished.
        }
    }

    private Task<bool> FailAsync(JobRecord job, string error, CancellationToken ct) =>
        _store.FailAsync(job.Id, Owner, Options.MaxAttempts, Options.RetryDelayFor(job.Attempts), error, ct);
}
