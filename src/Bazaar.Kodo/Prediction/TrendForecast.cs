namespace Bazaar.Kodo.Prediction;

/// <summary>What a forecast concluded.</summary>
public enum ForecastOutcome
{
    /// <summary>No forecast could be made. See <see cref="TrendForecast.Cause"/>. Never read as healthy.</summary>
    Unknown,

    /// <summary>The trend is flat, moving away from the threshold, or too slow to reach it within the forecast cap.</summary>
    NotApproaching,

    /// <summary>The series is already at or past the threshold. The deterministic rules own that state.</summary>
    Breached,

    /// <summary>The trend reaches the threshold. See the estimate and its band.</summary>
    Projected,
}

/// <summary>How far the band around an estimate can be trusted.</summary>
public enum ForecastConfidence
{
    Unknown,
    Low,
    Medium,
    High,
}

/// <summary>
/// A time-to-threshold forecast. The estimate comes with a band: <see cref="Earliest"/> (10th percentile of the
/// slope, steepest) and <see cref="Latest"/> (90th, shallowest). A null <see cref="Latest"/> means the shallow end of
/// the band never reaches the threshold. Alert text states the band, not a point (doc 46 13.2).
/// </summary>
public sealed record TrendForecast(
    ForecastOutcome Outcome,
    ForecastConfidence Confidence,
    TimeSpan? Estimate,
    TimeSpan? Earliest,
    TimeSpan? Latest,
    int SampleCount,
    DateTimeOffset ComputedAt,
    string? Cause = null)
{
    public static TrendForecast Unknown(string cause, int sampleCount, DateTimeOffset now) =>
        new(ForecastOutcome.Unknown, ForecastConfidence.Unknown, null, null, null, sampleCount, now, cause);
}
