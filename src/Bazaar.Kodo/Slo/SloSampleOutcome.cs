using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Slo;

/// <summary>
/// What one sample says about a service (doc 46 section 12.3). <see cref="Unobserved"/> is its own value, not a kind of
/// <see cref="Good"/>: time Kodo could not see is never counted as good (section 8.3).
/// </summary>
public enum SloSampleOutcome
{
    /// <summary>The service met the indicator.</summary>
    Good,

    /// <summary>The service did not meet the indicator.</summary>
    Bad,

    /// <summary>Nothing was observed, or the observation cannot be trusted. Consumes budget like <see cref="Bad"/>.</summary>
    Unobserved,
}

/// <summary>Turns raw readings into <see cref="SloSampleOutcome"/>. Every doubtful input becomes <see cref="SloSampleOutcome.Unobserved"/>.</summary>
public static class SloSamples
{
    /// <summary>A readiness reading: true is good, false is bad, null (no answer) is unobserved.</summary>
    public static SloSampleOutcome FromReadiness(bool? ready) =>
        ready switch
        {
            true => SloSampleOutcome.Good,
            false => SloSampleOutcome.Bad,
            null => SloSampleOutcome.Unobserved,
        };

    /// <summary>
    /// A health state sample. Healthy and degraded are good (the service still answers), failing and recovering are bad. Frozen is never
    /// an underlying state, so a frozen value, a missing value or an undefined value is unobserved.
    /// </summary>
    public static SloSampleOutcome FromState(KodoState? state) =>
        state switch
        {
            KodoState.Healthy or KodoState.Degraded => SloSampleOutcome.Good,
            KodoState.Failing or KodoState.Recovering => SloSampleOutcome.Bad,
            _ => SloSampleOutcome.Unobserved,
        };

    /// <summary>
    /// A measured duration against a limit (a latency or an age). At or under the limit is good, over is bad. A missing or negative value
    /// is unobserved: a timed-out request is reported by the caller as a duration over the limit, not as a missing one.
    /// </summary>
    public static SloSampleOutcome FromDuration(TimeSpan? measured, TimeSpan limit)
    {
        if (measured is null || measured.Value < TimeSpan.Zero)
        {
            return SloSampleOutcome.Unobserved;
        }

        return measured.Value <= limit ? SloSampleOutcome.Good : SloSampleOutcome.Bad;
    }
}
