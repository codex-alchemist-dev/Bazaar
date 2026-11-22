namespace Bazaar.Server.Modules.Health;

/// <summary>
/// Runs one probe under a timeout and turns every failure into a <see cref="ModuleHealthStatus.Failing"/> result.
/// A timeout gives "probe timed out". A thrown exception gives its type name only, never its message, so no secret
/// in an exception text reaches the health record. The timer comes from the injected <see cref="TimeProvider"/>.
/// The probe is raced against the timer, so a probe that ignores its cancellation token still cannot hang the caller.
/// </summary>
public static class ModuleHealthProbeRunner
{
    /// <summary>The default probe timeout when the caller passes none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="probe"/> with a timeout. Returns its result, or a Failing result on timeout or exception.
    /// Throws <see cref="OperationCanceledException"/> only when the caller's own token is cancelled.
    /// </summary>
    /// <param name="probe">The probe to run.</param>
    /// <param name="timeProvider">The clock and timer source.</param>
    /// <param name="timeout">The timeout, or null for <see cref="DefaultTimeout"/>. Must be positive.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static async ValueTask<ModuleHealthCheck> RunAsync(
        Func<CancellationToken, ValueTask<ModuleHealthCheck>> probe,
        TimeProvider timeProvider,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var limit = timeout ?? DefaultTimeout;
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), limit, "Probe timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = timeProvider.CreateTimer(_ => timedOut.TrySetResult(), null, limit, Timeout.InfiniteTimeSpan);

        // ExecuteAsync never throws, so the work task never faults unobserved after a timeout.
        // Task.Run: a probe that blocks its thread before its first await must not block the caller, or the timer could never win.
        var work = Task.Run(() => ExecuteAsync(probe, timeProvider, probeCancellation.Token).AsTask(), CancellationToken.None);
        var winner = await Task.WhenAny(work, timedOut.Task).ConfigureAwait(false);

        if (winner == work)
        {
            var result = await work.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        // The timer won. Ask the probe to stop; its late result, if any, is discarded.
        probeCancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return Failing("probe timed out", timeProvider);
    }

    private static async ValueTask<ModuleHealthCheck> ExecuteAsync(
        Func<CancellationToken, ValueTask<ModuleHealthCheck>> probe,
        TimeProvider timeProvider,
        CancellationToken probeToken)
    {
        try
        {
            return await probe(probeToken).ConfigureAwait(false)
                ?? Failing("probe returned no result", timeProvider);
        }
        catch (Exception exception)
        {
            // The type name only. The message can carry connection strings, paths or other secrets.
            return Failing($"probe threw {exception.GetType().Name}", timeProvider);
        }
    }

    private static ModuleHealthCheck Failing(string reason, TimeProvider timeProvider) =>
        new(ModuleHealthStatus.Failing, reason, timeProvider.GetUtcNow());
}
