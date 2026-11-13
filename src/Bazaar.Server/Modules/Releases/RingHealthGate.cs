namespace Bazaar.Server.Modules.Releases;

/// <summary>Kodo's health status for one install's sample (doc 46; the gate reads green, amber or red).</summary>
public enum KodoHealthStatus
{
    Green,
    Amber,
    Red,
}

/// <summary>One health sample from an install on a ring. The gate reads the list length as the sample count.</summary>
public sealed record HealthSample(ReleaseRing Ring, KodoHealthStatus Status, double ErrorRate);

/// <summary>What the gate says about a ring. Halt is terminal for the version (see <see cref="ReleaseCandidateLifecycle"/>).</summary>
public enum GateDecision
{
    Continue,
    Hold,
    Halt,
}

/// <summary>
/// Gate thresholds. The defaults are PLANNED operator settings (doc 49 section 6), not decided values.
/// </summary>
/// <param name="MinimumSamples">Samples needed before the gate may continue. Below this it holds.</param>
/// <param name="MaxErrorRate">Mean error rate above which the ring halts. A rate equal to this value does not halt.</param>
public sealed record GateThresholds(int MinimumSamples = 30, double MaxErrorRate = 0.01);

/// <summary>
/// Health gate for one ring. Pure: the caller supplies the samples and the thresholds.
/// Order of checks: any red sample or a mean error rate above the threshold halts, whatever the sample count (fail
/// closed). Otherwise fewer than the minimum samples holds, and so does any amber sample. Only green with enough samples continues.
/// </summary>
public static class RingHealthGate
{
    public static GateDecision Evaluate(ReleaseRing ring, IReadOnlyList<HealthSample> samples, GateThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(thresholds);
        ValidateThresholds(thresholds);

        foreach (var sample in samples)
        {
            if (sample.Ring != ring)
            {
                throw new ArgumentException($"Sample for ring {sample.Ring} passed to the gate for ring {ring}.", nameof(samples));
            }

            if (!double.IsFinite(sample.ErrorRate) || sample.ErrorRate < 0 || sample.ErrorRate > 1)
            {
                throw new ArgumentException("Error rate must be a finite value from 0 to 1.", nameof(samples));
            }
        }

        if (samples.Any(s => s.Status == KodoHealthStatus.Red))
        {
            return GateDecision.Halt;
        }

        if (samples.Count > 0 && MeanErrorRate(samples) > thresholds.MaxErrorRate)
        {
            return GateDecision.Halt;
        }

        if (samples.Count < thresholds.MinimumSamples)
        {
            return GateDecision.Hold;
        }

        if (samples.Any(s => s.Status == KodoHealthStatus.Amber))
        {
            return GateDecision.Hold;
        }

        return GateDecision.Continue;
    }

    // Summed in list order so the result does not depend on anything but the input.
    private static double MeanErrorRate(IReadOnlyList<HealthSample> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample.ErrorRate;
        }

        return sum / samples.Count;
    }

    private static void ValidateThresholds(GateThresholds thresholds)
    {
        if (thresholds.MinimumSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholds), thresholds.MinimumSamples, "MinimumSamples must be at least 1.");
        }

        if (!double.IsFinite(thresholds.MaxErrorRate) || thresholds.MaxErrorRate < 0 || thresholds.MaxErrorRate > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholds), thresholds.MaxErrorRate, "MaxErrorRate must be a finite value from 0 to 1.");
        }
    }
}
