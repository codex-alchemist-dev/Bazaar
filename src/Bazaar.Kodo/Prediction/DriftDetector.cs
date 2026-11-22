namespace Bazaar.Kodo.Prediction;

/// <summary>Where a series sits against its learned baseline.</summary>
public enum DriftState
{
    /// <summary>No assessment is possible (warming up or a rejected sample). See the cause. Never read as healthy.</summary>
    Unknown,

    Stable,
    DriftUp,
    DriftDown,
}

/// <summary>
/// A drift assessment. <see cref="StartedAt"/> is when the excursion began and <see cref="Magnitude"/> is the CUSUM
/// statistic in scale units. It is only ever shown beside the cause, never as a score on its own.
/// </summary>
public sealed record DriftAssessment(DriftState State, string? Cause, DateTimeOffset? StartedAt, double Magnitude)
{
    public static DriftAssessment Unknown(string cause) => new(DriftState.Unknown, cause, null, 0);
}

/// <summary>
/// Two-sided CUSUM on standardised residuals (doc 46 13.3; Page-Hinkley is not built, see 13.6). The residual is clamped
/// so one outlier cannot raise an alarm, and each statistic is capped so state stays bounded. Not thread safe.
/// </summary>
public sealed class DriftDetector
{
    private const double _capFactor = 10;

    private readonly BaselineOptions _options;
    private double _positive;
    private double _negative;
    private long? _positiveStart;
    private long? _negativeStart;

    public DriftDetector(BaselineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>Feeds one residual (value minus baseline median, in scale units) and returns the assessment.</summary>
    public DriftAssessment Update(DateTimeOffset at, double residual)
    {
        if (!double.IsFinite(residual))
        {
            return DriftAssessment.Unknown(PredictionCauses.InvalidSample);
        }

        double z = Math.Clamp(residual, -_options.ResidualClamp, _options.ResidualClamp);
        double cap = _options.CusumThreshold * _capFactor;
        _positive = Math.Min(cap, Math.Max(0, _positive + z - _options.CusumSlack));
        _negative = Math.Min(cap, Math.Max(0, _negative - z - _options.CusumSlack));
        _positiveStart = Track(_positive, _positiveStart, at);
        _negativeStart = Track(_negative, _negativeStart, at);

        bool up = _positive > _options.CusumThreshold;
        bool down = _negative > _options.CusumThreshold;
        if (up && (!down || _positive >= _negative))
        {
            return new DriftAssessment(DriftState.DriftUp, PredictionCauses.DriftUp, FromTicks(_positiveStart), _positive);
        }

        if (down)
        {
            return new DriftAssessment(DriftState.DriftDown, PredictionCauses.DriftDown, FromTicks(_negativeStart), _negative);
        }

        return new DriftAssessment(DriftState.Stable, null, null, 0);
    }

    public DriftMemory ToMemory() => new(_positive, _negative, _positiveStart, _negativeStart);

    public static bool TryRestore(DriftMemory? memory, BaselineOptions options, out DriftDetector? detector)
    {
        detector = null;
        double cap = options.CusumThreshold * _capFactor;
        if (memory is null
            || !double.IsFinite(memory.Positive) || memory.Positive < 0 || memory.Positive > cap
            || !double.IsFinite(memory.Negative) || memory.Negative < 0 || memory.Negative > cap
            || !ValidStart(memory.PositiveStart, memory.Positive)
            || !ValidStart(memory.NegativeStart, memory.Negative))
        {
            return false;
        }

        detector = new DriftDetector(options)
        {
            _positive = memory.Positive,
            _negative = memory.Negative,
            _positiveStart = memory.PositiveStart,
            _negativeStart = memory.NegativeStart,
        };
        return true;
    }

    private static bool ValidStart(long? start, double statistic) =>
        start is null ? statistic == 0 : statistic > 0 && start >= DateTime.MinValue.Ticks && start <= DateTime.MaxValue.Ticks;

    private static long? Track(double statistic, long? start, DateTimeOffset at) =>
        statistic <= 0 ? null : start ?? at.UtcTicks;

    private static DateTimeOffset? FromTicks(long? ticks) =>
        ticks is { } t ? new DateTimeOffset(t, TimeSpan.Zero) : null;
}
