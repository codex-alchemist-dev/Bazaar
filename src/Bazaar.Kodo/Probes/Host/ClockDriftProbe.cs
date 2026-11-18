namespace Bazaar.Kodo.Probes.Host;

/// <summary>
/// <c>host.clock.drift</c> (doc 46 section 9.1), critical past its hard limit. Reads the signed offset of the local
/// clock from the reference source and compares its absolute value with the thresholds. Section 9.1 names no
/// numbers, so both defaults are PROPOSED.
/// </summary>
public sealed class ClockDriftProbe
{
    /// <summary>PROPOSED warning offset. Operator setting once the Kodo settings registry carries it.</summary>
    public static readonly TimeSpan DefaultWarn = TimeSpan.FromMilliseconds(500);

    /// <summary>PROPOSED hard limit. Operator setting once the Kodo settings registry carries it.</summary>
    public static readonly TimeSpan DefaultHard = TimeSpan.FromSeconds(5);

    public const string Id = "host.clock.drift";

    private readonly IHostReadings _readings;

    public ClockDriftProbe(IHostReadings readings, ClockDriftThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(readings);

        _readings = readings;
        Thresholds = thresholds ?? new ClockDriftThresholds(DefaultWarn, DefaultHard);
    }

    public ClockDriftThresholds Thresholds { get; }

    /// <summary>Reads the offset and returns the observation. Read failures are unknown, never green.</summary>
    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan offset;
        try
        {
            offset = await _readings.ReadClockOffsetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unknown(ProbeCauses.Unreadable);
        }

        return Evaluate(offset, Thresholds);
    }

    /// <summary>
    /// The pure rule on the absolute offset. Both signs are compared without calling <see cref="TimeSpan.Duration"/>,
    /// which throws for <see cref="TimeSpan.MinValue"/>.
    /// </summary>
    public static ProbeResult Evaluate(TimeSpan offset, ClockDriftThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        if (offset >= thresholds.Hard || offset <= -thresholds.Hard)
        {
            return ProbeResult.Red(ProbeCauses.ClockDriftHard);
        }

        return offset >= thresholds.Warn || offset <= -thresholds.Warn
            ? ProbeResult.Amber(ProbeCauses.ClockDriftWarn)
            : ProbeResult.Green();
    }
}
