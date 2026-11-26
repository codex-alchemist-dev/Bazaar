namespace Bazaar.Kodo.Rings;

/// <summary>A module health contract failure seen on a cohort (doc 46 section 10).</summary>
/// <param name="ModuleId">The module id, a component id.</param>
/// <param name="Code">The fixed failure code.</param>
/// <param name="Critical">True when the module is a critical one.</param>
public sealed record ContractFailure(string ModuleId, string Code, bool Critical);

/// <summary>
/// What Kodo knows about one cohort over one window (doc 46 section 16.6). The same shape describes the new release's ring and its
/// baseline. Counts only, never free text; the collectors that fill it are not part of this core.
/// </summary>
/// <param name="ReleaseId">The release the cohort runs.</param>
/// <param name="Ring">The ring of the cohort. For a baseline, the ring it stood in.</param>
/// <param name="WindowStart">Start of the observed window.</param>
/// <param name="WindowEnd">End of the observed window.</param>
/// <param name="Devices">Devices that reported in the window.</param>
/// <param name="Sessions">Client sessions in the window.</param>
/// <param name="Crashes">Crashes in the window.</param>
/// <param name="ErrorEvents">Error-code events from the client capture (doc 41) in the window.</param>
/// <param name="StreamAttempts">Stream connection attempts in the window.</param>
/// <param name="StreamFailures">Failed stream attempts in the window.</param>
/// <param name="ContractFailures">Module contract failures seen on the cohort's version.</param>
/// <param name="SloBurnRate">The server SLO burn rate while the ring is live (1.0 spends the budget exactly on time).</param>
/// <param name="SelfTestsRun">Self-tests the new version ran.</param>
/// <param name="SelfTestsFailed">Self-tests that failed.</param>
/// <param name="RedCriticalProbes">Critical probes red right now on servers running this version, as component ids.</param>
public sealed record CohortSignals(
    string ReleaseId,
    ReleaseRing Ring,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int Devices,
    long Sessions,
    long Crashes,
    long ErrorEvents,
    long StreamAttempts,
    long StreamFailures,
    IReadOnlyList<ContractFailure> ContractFailures,
    decimal SloBurnRate,
    int SelfTestsRun,
    int SelfTestsFailed,
    IReadOnlyList<string> RedCriticalProbes)
{
    /// <summary>The window length.</summary>
    public TimeSpan Window => WindowEnd - WindowStart;

    /// <summary>Crashes per 1,000 sessions, or 0 with no sessions.</summary>
    public decimal CrashRatePer1000 => Per1000(Crashes, Sessions);

    /// <summary>Error events per 1,000 sessions, or 0 with no sessions.</summary>
    public decimal ErrorRatePer1000 => Per1000(ErrorEvents, Sessions);

    /// <summary>Failed stream attempts per 1,000 attempts, or 0 with no attempts.</summary>
    public decimal StreamFailureRatePer1000 => Per1000(StreamFailures, StreamAttempts);

    /// <summary>True when the counts are consistent: nothing negative, no null list, a forward window, failures within attempts.</summary>
    public bool IsWellFormed =>
        ContractFailures is not null && RedCriticalProbes is not null
        && !string.IsNullOrEmpty(ReleaseId) && Enum.IsDefined(Ring)
        && WindowEnd >= WindowStart
        && Devices >= 0 && Sessions >= 0 && Crashes >= 0 && ErrorEvents >= 0 && StreamAttempts >= 0 && StreamFailures >= 0
        && StreamFailures <= StreamAttempts
        && SloBurnRate >= 0 && SelfTestsRun >= 0 && SelfTestsFailed >= 0 && SelfTestsFailed <= SelfTestsRun
        && ContractFailures.All(failure => failure is not null && failure.ModuleId is not null && failure.Code is not null)
        && RedCriticalProbes.All(probe => probe is not null);

    private static decimal Per1000(long events, long denominator) => denominator == 0 ? 0m : events * 1000m / denominator;
}
