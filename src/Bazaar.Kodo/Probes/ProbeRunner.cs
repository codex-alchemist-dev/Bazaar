namespace Bazaar.Kodo.Probes;

/// <summary>
/// Runs one probe through its breaker and its pool (doc 46 section 7). It always returns a <see cref="ProbeResult"/>:
/// an open breaker yields unknown with cause <c>breaker_open</c>, a timeout or a full pool yields unknown, and an
/// exception yields red with cause <c>probe_exception</c>. Only the caller's own cancellation is thrown.
/// </summary>
public sealed class ProbeRunner
{
    private readonly Bulkhead _bulkhead;
    private readonly CircuitBreaker _breaker;

    public ProbeRunner(Bulkhead bulkhead, CircuitBreaker breaker)
    {
        _bulkhead = bulkhead ?? throw new ArgumentNullException(nameof(bulkhead));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
    }

    public async Task<ProbeResult> RunAsync(
        Func<CancellationToken, Task<ProbeResult>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!_breaker.TryEnter(out BreakerTicket ticket))
        {
            return ProbeResult.BreakerOpen();
        }

        // A call that never reached the target does not say anything about the target. It must not count
        // as a success (which would close a half-open breaker) or as a failure, so its trial slot is released.
        bool recorded = false;
        try
        {
            ProbeResult result = await _bulkhead.RunAsync(probe, timeout, cancellationToken).ConfigureAwait(false);
            if (result.Cause == ProbeCauses.BulkheadFull)
            {
                _breaker.ReleaseTrial(ticket);
            }
            else
            {
                _breaker.Record(ticket, result.IsFailure);
            }

            recorded = true;
            return result;
        }
        finally
        {
            // Reached only when the caller cancelled, so the trial is released and never left in flight.
            if (!recorded)
            {
                _breaker.ReleaseTrial(ticket);
            }
        }
    }
}
