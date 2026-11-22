namespace Bazaar.Kodo.Prediction;

/// <summary>
/// Robust linear trend forecaster (doc 46 section 13.2, choices in 13.6). It fits a Theil-Sen line (the median of all
/// pairwise slopes) to a bounded window, so a few outliers do not move the trend. The 10th and 90th percentile slopes
/// give the band. Pure and deterministic: the clock is a parameter. Not enough data, bad samples and stale series all
/// come back as <see cref="ForecastOutcome.Unknown"/> with a cause, never as green.
/// </summary>
public static class TrendForecaster
{
    private const double _bandLow = 0.10;
    private const double _bandHigh = 0.90;
    private const double _highConfidenceSpread = 0.5;
    private const double _mediumConfidenceSpread = 1.5;

    public static TrendForecast Forecast(
        IReadOnlyList<TrendSample> samples,
        double threshold,
        ThresholdDirection direction,
        TrendOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!double.IsFinite(threshold) || Math.Abs(threshold) > options.MaxAbsValue)
        {
            return TrendForecast.Unknown(PredictionCauses.InvalidSample, samples.Count, now);
        }

        string? invalid = FindInvalid(samples, options, now);
        if (invalid is not null)
        {
            return TrendForecast.Unknown(invalid, samples.Count, now);
        }

        DateTimeOffset cutoff = RobustStats.Before(now, options.Window);
        var window = samples.Where(s => s.At >= cutoff).TakeLast(options.MaxSamples).ToArray();
        if (window.Length < options.MinSamples || window[^1].At - window[0].At < options.MinSpan)
        {
            return TrendForecast.Unknown(PredictionCauses.InsufficientData, window.Length, now);
        }

        if (now - window[^1].At > options.StaleAfter)
        {
            return TrendForecast.Unknown(PredictionCauses.StaleData, window.Length, now);
        }

        double sign = direction == ThresholdDirection.Rising ? 1 : -1;
        TrendSample last = window[^1];
        if (sign * (last.Value - threshold) >= 0)
        {
            return new TrendForecast(
                ForecastOutcome.Breached, ForecastConfidence.High, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, window.Length, now);
        }

        // Slopes are in value units per second, signed so that positive means towards the threshold.
        double[] slopes = PairwiseSlopes(window, sign);
        Array.Sort(slopes);
        double median = RobustStats.MedianSorted(slopes);
        double fast = RobustStats.PercentileSorted(slopes, _bandHigh);
        double slow = RobustStats.PercentileSorted(slopes, _bandLow);

        double level = FittedLevelAtLast(window, sign, median);
        double distance = Math.Max(0, sign * (threshold - level));

        if (median <= 0)
        {
            ForecastConfidence flat = fast <= 0 ? ForecastConfidence.High : ForecastConfidence.Medium;
            return new TrendForecast(ForecastOutcome.NotApproaching, flat, null, null, null, window.Length, now);
        }

        double cap = options.MaxForecast.TotalSeconds;
        double estimate = distance / median;
        if (!double.IsFinite(estimate) || estimate > cap)
        {
            return new TrendForecast(
                ForecastOutcome.NotApproaching, ForecastConfidence.Medium, null, null, null, window.Length, now);
        }

        double earliest = Math.Min(distance / fast, estimate);
        double? latest = slow > 0 && distance / slow <= cap ? distance / slow : null;
        return new TrendForecast(
            ForecastOutcome.Projected,
            Classify(estimate, earliest, latest),
            TimeSpan.FromSeconds(estimate),
            TimeSpan.FromSeconds(earliest),
            latest is null ? null : TimeSpan.FromSeconds(latest.Value),
            window.Length,
            now);
    }

    private static string? FindInvalid(IReadOnlyList<TrendSample> samples, TrendOptions options, DateTimeOffset now)
    {
        for (int i = 0; i < samples.Count; i++)
        {
            TrendSample s = samples[i];
            if (!double.IsFinite(s.Value) || Math.Abs(s.Value) > options.MaxAbsValue)
            {
                return PredictionCauses.InvalidSample;
            }

            if (i > 0 && s.At <= samples[i - 1].At)
            {
                return PredictionCauses.OutOfOrderSample;
            }

            if (s.At - now > options.FutureTolerance)
            {
                return PredictionCauses.FutureSample;
            }
        }

        return null;
    }

    private static double[] PairwiseSlopes(TrendSample[] window, double sign)
    {
        int n = window.Length;
        var slopes = new double[n * (n - 1) / 2];
        int k = 0;
        for (int i = 0; i < n - 1; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double dt = (window[j].At - window[i].At).TotalSeconds;
                slopes[k++] = sign * (window[j].Value - window[i].Value) / dt;
            }
        }

        return slopes;
    }

    private static double FittedLevelAtLast(TrendSample[] window, double sign, double signedSlope)
    {
        DateTimeOffset lastAt = window[^1].At;
        var intercepts = new double[window.Length];
        for (int i = 0; i < window.Length; i++)
        {
            double x = (window[i].At - lastAt).TotalSeconds;
            intercepts[i] = (sign * window[i].Value) - (signedSlope * x);
        }

        return sign * RobustStats.Median(intercepts);
    }

    private static ForecastConfidence Classify(double estimate, double earliest, double? latest)
    {
        if (latest is null)
        {
            return ForecastConfidence.Low;
        }

        if (estimate <= 0)
        {
            return ForecastConfidence.High;
        }

        double spread = (latest.Value - earliest) / estimate;
        if (spread <= _highConfidenceSpread)
        {
            return ForecastConfidence.High;
        }

        return spread <= _mediumConfidenceSpread ? ForecastConfidence.Medium : ForecastConfidence.Low;
    }
}
