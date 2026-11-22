using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Prediction;

/// <summary>
/// Turns forecasts and drift assessments into <see cref="ProbeResult"/>. Learned signals raise amber at most and never
/// red (doc 46 13.1). Unknown stays unknown with its cause. A numeric score is never the result on its own.
/// </summary>
public static class PredictionProbeMapper
{
    public static ProbeResult ToProbeResult(TrendForecast forecast, TrendOptions options)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(options);

        switch (forecast.Outcome)
        {
            case ForecastOutcome.Unknown:
                return ProbeResult.Unknown(forecast.Cause ?? PredictionCauses.InsufficientData);
            case ForecastOutcome.Breached:
                return ProbeResult.Amber(PredictionCauses.ForecastThresholdSoon);
            case ForecastOutcome.Projected:
                bool trusted = forecast.Confidence is ForecastConfidence.Medium or ForecastConfidence.High;
                bool soon = forecast.Estimate is { } estimate && estimate <= options.AmberHorizon;
                return trusted && soon ? ProbeResult.Amber(PredictionCauses.ForecastThresholdSoon) : ProbeResult.Green();
            default:
                return ProbeResult.Green();
        }
    }

    public static ProbeResult ToProbeResult(DriftAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return assessment.State switch
        {
            DriftState.Stable => ProbeResult.Green(),
            DriftState.DriftUp => ProbeResult.Amber(PredictionCauses.DriftUp),
            DriftState.DriftDown => ProbeResult.Amber(PredictionCauses.DriftDown),
            _ => ProbeResult.Unknown(assessment.Cause ?? PredictionCauses.BaselineWarmingUp),
        };
    }
}
