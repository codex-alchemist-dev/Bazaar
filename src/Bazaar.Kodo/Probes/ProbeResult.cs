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
