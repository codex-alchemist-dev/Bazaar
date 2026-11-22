namespace Bazaar.Kodo.Prediction;

/// <summary>
/// Cause strings the predictor writes into <see cref="Bazaar.Kodo.Probes.ProbeResult.Cause"/> (doc 46 sections 13 and 13.6).
/// They are stable strings, like <see cref="Bazaar.Kodo.Probes.ProbeCauses"/>. Learned signals raise amber at most (section 13.1).
/// </summary>
public static class PredictionCauses
{
    /// <summary>Too few samples, or too short a span, to forecast. Unknown, never green (section 13.2).</summary>
    public const string InsufficientData = "insufficient_data";

    /// <summary>A sample or a threshold was NaN, infinite or outside the accepted range.</summary>
    public const string InvalidSample = "invalid_sample";

    /// <summary>A sample was not newer than the previous one.</summary>
    public const string OutOfOrderSample = "out_of_order_sample";

    /// <summary>A sample is stamped later than now plus the tolerated clock skew.</summary>
    public const string FutureSample = "future_sample";

    /// <summary>The newest sample is older than the stale limit, so no forecast is made.</summary>
    public const string StaleData = "stale_data";

    /// <summary>The trend reaches the threshold within the amber horizon (advisory, section 13.1).</summary>
    public const string ForecastThresholdSoon = "forecast_threshold_soon";

    /// <summary>The baseline for this hour of the week has too little history (section 13.3).</summary>
    public const string BaselineWarmingUp = "baseline_warming_up";

    /// <summary>The series drifted above its learned baseline (two-sided CUSUM).</summary>
    public const string DriftUp = "drift_up";

    /// <summary>The series drifted below its learned baseline (two-sided CUSUM).</summary>
    public const string DriftDown = "drift_down";
}
