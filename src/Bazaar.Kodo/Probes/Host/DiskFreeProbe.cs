namespace Bazaar.Kodo.Probes.Host;

/// <summary>
/// <c>host.disk.free.&lt;volume&gt;</c> (doc 46 section 9.1). Reads one volume and compares its used percent with the
/// thresholds. The warning threshold is the doc 03 setting <c>host.health.disk_used_warn_percent</c> (default 80).
/// The high threshold has no doc value yet and its default is PROPOSED.
/// </summary>
public sealed class DiskFreeProbe
{
    /// <summary>The doc 03 default for <c>host.health.disk_used_warn_percent</c>.</summary>
    public const double DefaultWarnUsedPercent = 80;

    /// <summary>PROPOSED high threshold. Operator setting once the Kodo settings registry carries it.</summary>
    public const double DefaultHighUsedPercent = 95;

    private readonly IHostReadings _readings;
    private readonly string _volume;

    public DiskFreeProbe(IHostReadings readings, string volume, UsedPercentThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        _readings = readings;
        _volume = volume;
        Thresholds = thresholds ?? new UsedPercentThresholds(DefaultWarnUsedPercent, DefaultHighUsedPercent);
        Id = "host.disk.free." + volume;
    }

    /// <summary>The probe id, with the volume name, as in section 9.1.</summary>
    public string Id { get; }

    public UsedPercentThresholds Thresholds { get; }

    /// <summary>Reads the volume and returns the observation. Read failures are unknown, never green.</summary>
    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        DiskReading reading;
        try
        {
            reading = await _readings.ReadDiskAsync(_volume, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unknown(ProbeCauses.Unreadable);
        }

        return Evaluate(reading, Thresholds);
    }

    /// <summary>The pure rule: used percent against the thresholds. An impossible reading is unknown.</summary>
    public static ProbeResult Evaluate(DiskReading reading, UsedPercentThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        if (reading.TotalBytes <= 0 || reading.FreeBytes < 0 || reading.FreeBytes > reading.TotalBytes)
        {
            return ProbeResult.Unknown(ProbeCauses.Unreadable);
        }

        double usedPercent = (double)(reading.TotalBytes - reading.FreeBytes) * 100 / reading.TotalBytes;
        if (usedPercent >= thresholds.High)
        {
            return ProbeResult.Red(ProbeCauses.DiskUsageHigh);
        }

        return usedPercent >= thresholds.Warn ? ProbeResult.Amber(ProbeCauses.DiskUsageWarn) : ProbeResult.Green();
    }
}
