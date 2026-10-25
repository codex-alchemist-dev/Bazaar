using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Gateway.Health;

/// <summary>Runs one active probe and records its outcome on the key's breaker.</summary>
public static class HealthProbeRunner
{
    /// <summary>
    /// Runs the probe when the breaker admits a call. Closed runs it as a plain check. Open runs it only after the
    /// open duration has passed, and HalfOpen runs it as the trial. Returns null, without calling the probe, when
    /// the breaker is still open or has no trial left. A probe that throws (other than the caller's cancellation)
    /// is recorded as unhealthy. A cancelled probe releases its trial and rethrows.
    /// </summary>
    public static async Task<ProbeResult?> RunAsync(
        ProviderKey key,
        ProviderEndpoint endpoint,
        IHealthProbe probe,
        HealthTracker tracker,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(tracker);

        if (!tracker.TryAcquire(key))
        {
            return null;
        }

        ProbeResult result;
        try
        {
            result = await probe.ProbeAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            tracker.Abandon(key);
            throw;
        }
        catch (Exception ex)
        {
            // The exception type is recorded, not its message, so nothing from the upstream or the key can leak into the result.
            result = new ProbeResult(false, TimeSpan.Zero, $"probe failed: {ex.GetType().Name}");
        }

        tracker.RecordProbe(key, result);
        return result;
    }
}
