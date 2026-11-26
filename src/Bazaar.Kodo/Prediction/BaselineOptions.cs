namespace Bazaar.Kodo.Prediction;

/// <summary>
/// Baseline and drift settings. Median and MAD over the trailing weeks per hour-of-week bucket, and a two-sided CUSUM
/// are doc 46 section 13.3. The numbers are PROPOSED in section 13.6 and are operator settings once the registry carries them.
/// </summary>
public sealed record BaselineOptions
{
    /// <summary>Number of hour-of-week buckets (7 x 24). Fixed.</summary>
    public const int BucketCount = 168;

    /// <summary>Trailing weeks kept per bucket (doc 46 13.3: 4).</summary>
    public int TrailingWeeks { get; init; } = 4;

    /// <summary>A bucket needs this many distinct weeks before it is used (doc 46 13.3: 3).</summary>
    public int MinWeeks { get; init; } = 3;

    /// <summary>MAD-to-sigma factor for normal data.</summary>
    public double MadScale { get; init; } = 1.4826;

    /// <summary>The scale never drops below this fraction of the median's magnitude. PROPOSED.</summary>
    public double RelativeScaleFloor { get; init; } = 0.05;

    /// <summary>The scale never drops below this value, in the series' own unit. Set per series. PROPOSED.</summary>
    public double AbsoluteScaleFloor { get; init; } = 1.0;

    /// <summary>CUSUM slack, in scale units. Standard value 0.5.</summary>
    public double CusumSlack { get; init; } = 0.5;

    /// <summary>CUSUM alarm level, in scale units. Standard value 5.</summary>
    public double CusumThreshold { get; init; } = 5.0;

    /// <summary>One residual counts for at most this many scale units, so one outlier cannot raise an alarm. PROPOSED.</summary>
    public double ResidualClamp { get; init; } = 4.0;

    /// <summary>Values with a larger magnitude are rejected. PROPOSED.</summary>
    public double MaxAbsValue { get; init; } = 1e15;

    public void Validate()
    {
        if (TrailingWeeks is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(TrailingWeeks));
        }

        if (MinWeeks < 1 || MinWeeks > TrailingWeeks)
        {
            throw new ArgumentOutOfRangeException(nameof(MinWeeks));
        }

        RequirePositive(MadScale, nameof(MadScale));
        RequirePositive(AbsoluteScaleFloor, nameof(AbsoluteScaleFloor));
        RequirePositive(CusumSlack, nameof(CusumSlack));
        RequirePositive(CusumThreshold, nameof(CusumThreshold));
        RequirePositive(ResidualClamp, nameof(ResidualClamp));
        RequirePositive(MaxAbsValue, nameof(MaxAbsValue));
        if (!double.IsFinite(RelativeScaleFloor) || RelativeScaleFloor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RelativeScaleFloor));
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
