namespace Bazaar.Kodo.Prediction;

/// <summary>Which way a series moves towards its threshold.</summary>
public enum ThresholdDirection
{
    /// <summary>The series breaches by rising to or past the threshold (disk used, queue depth, p95).</summary>
    Rising,

    /// <summary>The series breaches by falling to or below the threshold (disk free, memory free).</summary>
    Falling,
}

/// <summary>
/// Trend forecast settings. The window and the 72 hour minimum span are doc 46 section 13.2 defaults. The other values
/// are PROPOSED in section 13.6. Nothing here can make a state critical (section 13.1).
/// </summary>
public sealed record TrendOptions
{
    /// <summary>Sliding window. Older samples are dropped (doc 46 13.2: 14 days).</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Minimum time between the oldest and newest sample before any forecast (doc 46 13.2: 72 hours).</summary>
    public TimeSpan MinSpan { get; init; } = TimeSpan.FromHours(72);

    /// <summary>Minimum samples in the window. PROPOSED.</summary>
    public int MinSamples { get; init; } = 24;

    /// <summary>Hard cap on stored samples, which bounds memory and the pairwise fit. PROPOSED.</summary>
    public int MaxSamples { get; init; } = 256;

    /// <summary>A series whose newest sample is older than this is not forecast. PROPOSED.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How far past now a sample may be stamped. PROPOSED.</summary>
    public TimeSpan FutureTolerance { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>An estimate at or inside this horizon raises amber. PROPOSED.</summary>
    public TimeSpan AmberHorizon { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Estimates beyond this are treated as "not approaching". PROPOSED.</summary>
    public TimeSpan MaxForecast { get; init; } = TimeSpan.FromDays(365);

    /// <summary>Values or thresholds with a larger magnitude are rejected, so no arithmetic can overflow. PROPOSED.</summary>
    public double MaxAbsValue { get; init; } = 1e15;

    /// <summary>Throws when a value is out of range. Settings may tighten, never loosen below these checks.</summary>
    public void Validate()
    {
        if (Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Window));
        }

        if (MinSpan <= TimeSpan.Zero || MinSpan > Window)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSpan));
        }

        if (MinSamples < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSamples));
        }

        if (MaxSamples < MinSamples || MaxSamples > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSamples));
        }

        if (StaleAfter <= TimeSpan.Zero || FutureTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StaleAfter));
        }

        if (AmberHorizon <= TimeSpan.Zero || MaxForecast < AmberHorizon || MaxForecast > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(nameof(AmberHorizon));
        }

        if (!double.IsFinite(MaxAbsValue) || MaxAbsValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAbsValue));
        }
    }
}
