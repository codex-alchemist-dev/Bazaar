namespace Bazaar.Conduit;

/// <summary>Timing for <see cref="TunnelReadiness"/>. Defaults come from the live quick-tunnel spike (C2).</summary>
/// <param name="Window">Total time to keep trying before giving up. A fresh quick tunnel answered after about 65 s.</param>
/// <param name="FirstDelay">Delay after the first failed try.</param>
/// <param name="MaxDelay">Longest delay between tries.</param>
public sealed record TunnelReadinessOptions(TimeSpan Window, TimeSpan FirstDelay, TimeSpan MaxDelay)
{
    /// <summary>90 s window, 2 s first delay, 10 s cap (docs/spikes/C2).</summary>
    public static TunnelReadinessOptions Default { get; } = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
}

/// <summary>Outcome of waiting for a tunnel to become reachable.</summary>
/// <param name="Reachable">True when a try succeeded inside the window.</param>
/// <param name="Attempts">Number of tries made.</param>
/// <param name="Elapsed">Time spent, by the injected clock.</param>
/// <param name="LastError">The last failure message, or null when reachable.</param>
public sealed record TunnelReadinessResult(bool Reachable, int Attempts, TimeSpan Elapsed, string? LastError);

/// <summary>
/// Waits for a newly published tunnel address to start answering. A new quick tunnel is published in seconds but
/// its hostname answers only after about a minute, so a one-shot connect would wrongly call it broken. The try
/// itself (open a socket, send a probe) is supplied by the caller, so the wizard and clients share this policy.
/// Doc 39, testing section; evidence in docs/spikes/C2.
/// </summary>
public static class TunnelReadiness
{
    /// <summary>
    /// Calls <paramref name="tryConnect"/> until it returns true or <paramref name="options"/>.Window passes. A thrown
    /// exception counts as a failed try; cancellation propagates. Delays double from the first delay up to the cap.
    /// </summary>
    public static async Task<TunnelReadinessResult> WaitAsync(
        Func<CancellationToken, Task<bool>> tryConnect,
        TunnelReadinessOptions options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tryConnect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        var start = time.GetTimestamp();
        var delay = options.FirstDelay;
        var attempts = 0;
        string? lastError = null;
        while (true)
        {
            attempts++;
            try
            {
                if (await tryConnect(cancellationToken).ConfigureAwait(false))
                {
                    return new TunnelReadinessResult(true, attempts, time.GetElapsedTime(start), null);
                }

                lastError = "not-ready";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
            }

            var elapsed = time.GetElapsedTime(start);
            var remaining = options.Window - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new TunnelReadinessResult(false, attempts, elapsed, lastError);
            }

            await Task.Delay(delay < remaining ? delay : remaining, time, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxDelay.Ticks));
        }
    }
}
