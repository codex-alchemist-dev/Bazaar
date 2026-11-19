namespace Bazaar.Kodo.Probes.Host;

/// <summary>
/// <c>host.memory.pressure</c> (doc 46 section 9.1), non-critical. Compares used physical memory with the thresholds.
/// The growth trend in section 9.1 needs history and is not in this slice. Both default thresholds are PROPOSED.
/// </summary>
public sealed class MemoryPressureProbe
{
    /// <summary>PROPOSED warning threshold. Operator setting once the Kodo settings registry carries it.</summary>
    public const double DefaultWarnUsedPercent = 90;

    /// <summary>PROPOSED high threshold. Operator setting once the Kodo settings registry carries it.</summary>
    public const double DefaultHighUsedPercent = 97;

    public const string Id = "host.memory.pressure";

    private readonly IHostReadings _readings;

    public MemoryPressureProbe(IHostReadings readings, UsedPercentThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(readings);

        _readings = readings;
        Thresholds = thresholds ?? new UsedPercentThresholds(DefaultWarnUsedPercent, DefaultHighUsedPercent);
    }

    public UsedPercentThresholds Thresholds { get; }

    /// <summary>Reads memory and returns the observation. Read failures are unknown, never green.</summary>
    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        MemoryReading reading;
        try
        {
            reading = await _readings.ReadMemoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unknown(ProbeCauses.Unreadable);
        }

        return Evaluate(reading, Thresholds);
    }

    /// <summary>The pure rule: used memory percent against the thresholds. An impossible reading is unknown.</summary>
    public static ProbeResult Evaluate(MemoryReading reading, UsedPercentThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        if (reading.TotalBytes <= 0 || reading.AvailableBytes < 0 || reading.AvailableBytes > reading.TotalBytes)
        {
            return ProbeResult.Unknown(ProbeCauses.Unreadable);
        }

        double usedPercent = (double)(reading.TotalBytes - reading.AvailableBytes) * 100 / reading.TotalBytes;
        if (usedPercent >= thresholds.High)
        {
            return ProbeResult.Red(ProbeCauses.MemoryPressureHigh);
        }

        return usedPercent >= thresholds.Warn
            ? ProbeResult.Amber(ProbeCauses.MemoryPressureWarn)
            : ProbeResult.Green();
    }
}
