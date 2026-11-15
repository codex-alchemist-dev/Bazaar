namespace Bazaar.Kodo.Probes.Self;

/// <summary>
/// <c>kodo.self.spill</c> (doc 46 sections 7.5 and 9.1), non-critical. The reading is the age of the oldest unsent spill
/// record. No spill file is green. A sealed spill is red, and so is an unreadable one: a spill that cannot be read is never
/// reported green. An unsent record older than the warning age is amber. The default warning age is PROPOSED.
/// </summary>
public sealed class SpillAgeProbe
{
    /// <summary>PROPOSED warning age. Operator setting once the Kodo settings registry carries it.</summary>
    public static readonly TimeSpan DefaultWarnAge = TimeSpan.FromHours(1);

    public const string Id = "kodo.self.spill";

    private readonly IKodoSelfReadings _readings;
    private readonly TimeProvider _timeProvider;

    public SpillAgeProbe(IKodoSelfReadings readings, TimeSpan? warnAge = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(readings);

        TimeSpan age = warnAge ?? DefaultWarnAge;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(age, TimeSpan.Zero);

        _readings = readings;
        WarnAge = age;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan WarnAge { get; }

    /// <summary>Reads the spill state and returns the observation. An unreadable spill is red, never green.</summary>
    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        SpillFileReading? reading;
        try
        {
            reading = await _readings.ReadSpillFileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Red(ProbeCauses.Unreadable);
        }

        return Evaluate(reading, _timeProvider.GetUtcNow(), WarnAge);
    }

    /// <summary>The pure rule. A future timestamp is unknown, because the clock or the file cannot be trusted.</summary>
    public static ProbeResult Evaluate(SpillFileReading? reading, DateTimeOffset now, TimeSpan warnAge)
    {
        if (reading is not { } spill)
        {
            return ProbeResult.Green();
        }

        if (spill.Sealed)
        {
            return ProbeResult.Red(ProbeCauses.SpillSealed);
        }

        TimeSpan age = now - spill.OldestUnsentUtc;
        if (age < TimeSpan.Zero)
        {
            return ProbeResult.Unknown(ProbeCauses.SpillTimestampFuture);
        }

        return age >= warnAge ? ProbeResult.Amber(ProbeCauses.SpillAge) : ProbeResult.Green();
    }
}
