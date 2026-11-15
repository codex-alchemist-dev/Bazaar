namespace Bazaar.Kodo.Probes;

/// <summary>The status a probe reports. Unknown never counts as green (doc 46 section 9).</summary>
public enum ProbeStatus
{
    Green,
    Amber,
    Red,
    Unknown,
}

/// <summary>Cause strings that Kodo's probe runtime writes. They are part of the observation record.</summary>
public static class ProbeCauses
{
    /// <summary>The probe exceeded its timeout (doc 46 section 7.3).</summary>
    public const string Timeout = "timeout";

    /// <summary>The target's breaker is open, or its single half-open trial is already running (section 7.4).</summary>
    public const string BreakerOpen = "breaker_open";

    /// <summary>The probe's pool has no free slot. The probe never ran (section 7.3).</summary>
    public const string BulkheadFull = "bulkhead_full";

    /// <summary>The probe delegate threw an exception.</summary>
    public const string ProbeException = "probe_exception";

    /// <summary>The reading source could not read the value, or the reading is not valid. Never green (section 9).</summary>
    public const string Unreadable = "unreadable";

    /// <summary>Used space on a volume is at or past its warning threshold (section 9.1, <c>host.disk.free</c>).</summary>
    public const string DiskUsageWarn = "disk_usage_warn";

    /// <summary>Used space on a volume is at or past its high threshold (section 9.1, <c>host.disk.free</c>).</summary>
    public const string DiskUsageHigh = "disk_usage_high";

    /// <summary>Used memory is at or past its warning threshold (section 9.1, <c>host.memory.pressure</c>).</summary>
    public const string MemoryPressureWarn = "memory_pressure_warn";

    /// <summary>Used memory is at or past its high threshold (section 9.1, <c>host.memory.pressure</c>).</summary>
    public const string MemoryPressureHigh = "memory_pressure_high";

    /// <summary>The local clock is off by more than its warning offset (section 9.1, <c>host.clock.drift</c>).</summary>
    public const string ClockDriftWarn = "clock_drift_warn";

    /// <summary>The local clock is off by more than its hard limit (section 9.1, <c>host.clock.drift</c>).</summary>
    public const string ClockDriftHard = "clock_drift_hard";

    /// <summary>A test write to the journal directory was refused (section 9.1, <c>kodo.self.journal_writable</c>).</summary>
    public const string JournalNotWritable = "journal_not_writable";

    /// <summary>The oldest unreplayed spill record is older than the warning age (section 7.5, <c>kodo.self.spill</c>).</summary>
    public const string SpillAge = "spill_age";

    /// <summary>The oldest unreplayed spill record is stamped in the future, so the clock or the file is wrong.</summary>
    public const string SpillTimestampFuture = "spill_timestamp_future";
}

/// <summary>
/// One probe observation. <see cref="Cause"/> explains a non-green status; <see cref="Error"/> carries
/// the exception behind a <c>probe_exception</c> so the record is never silent about what failed.
/// </summary>
public sealed record ProbeResult(ProbeStatus Status, string? Cause = null, Exception? Error = null)
{
    public static ProbeResult Green() => new(ProbeStatus.Green);

    public static ProbeResult Amber(string cause) => new(ProbeStatus.Amber, cause);

    public static ProbeResult Red(string cause, Exception? error = null) => new(ProbeStatus.Red, cause, error);

    public static ProbeResult Unknown(string cause) => new(ProbeStatus.Unknown, cause);

    /// <summary>The probe result for an open breaker: unknown with cause <c>breaker_open</c>.</summary>
    public static ProbeResult BreakerOpen() => Unknown(ProbeCauses.BreakerOpen);

    /// <summary>The probe result for a timed-out probe: unknown with cause <c>timeout</c>, never green.</summary>
    public static ProbeResult Timeout() => Unknown(ProbeCauses.Timeout);

    /// <summary>The probe result for a thrown exception: red with cause <c>probe_exception</c>.</summary>
    public static ProbeResult FromException(Exception error) => Red(ProbeCauses.ProbeException, error);

    /// <summary>True when the result says the target failed (red, or a timeout). Breakers count these.</summary>
    public bool IsFailure => Status == ProbeStatus.Red || Cause == ProbeCauses.Timeout;
}
