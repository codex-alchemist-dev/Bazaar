using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;

namespace Bazaar.Kodo.Chaos;

/// <summary>What the monitor needs to judge one running experiment.</summary>
/// <param name="Experiment">The running experiment.</param>
/// <param name="StartedAt">When its fault began.</param>
/// <param name="Incident">The open incident, or null.</param>
/// <param name="Observation">The current observation, or null when it cannot be read. Null aborts.</param>
/// <param name="DataLossInvariantFailed">A data-loss invariant has failed.</param>
/// <param name="OwnerAbortRequested">The person asked to stop.</param>
public sealed record ChaosRunState(
    ChaosExperiment Experiment,
    DateTimeOffset StartedAt,
    IncidentRecord? Incident,
    KodoObservation? Observation,
    bool DataLossInvariantFailed,
    bool OwnerAbortRequested);

/// <summary>The monitor's answer. When <see cref="Abort"/> is true the fault must be reverted by <see cref="StopBy"/>.</summary>
/// <param name="Abort">Whether to stop now.</param>
/// <param name="Code">A <see cref="ChaosCauses"/> code, or <see cref="ChaosCauses.Allowed"/> to carry on.</param>
/// <param name="StopBy">The deadline for the revert, or null when not aborting.</param>
public sealed record ChaosAbortDecision(bool Abort, string Code, DateTimeOffset? StopBy);

/// <summary>
/// Decides whether a running experiment must stop (doc 46 section 17.3: abort within 10 seconds). Pure and fail-safe: an open incident,
/// any critical state, an unreadable state, a failed data-loss invariant, a clock that went backwards, an owner request or an expired
/// duration all abort. The host calls it on a tick well inside the abort deadline.
/// </summary>
public static class ChaosAbortMonitor
{
    /// <summary>Judges one experiment at <paramref name="now"/>. Checks run in a fixed order and the first match names the code.</summary>
    public static ChaosAbortDecision Check(ChaosRunState run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(run.Experiment);
        TimeSpan deadline = run.Experiment.AbortWithin > TimeSpan.Zero && run.Experiment.AbortWithin <= ChaosSettings.CeilingAbortWithin
            ? run.Experiment.AbortWithin
            : ChaosSettings.CeilingAbortWithin;

        string? code = null;
        if (IncidentFreeze.IsFrozen(run.Incident))
        {
            code = ChaosCauses.IncidentOpen;
        }
        else if (run.Observation is null)
        {
            code = ChaosCauses.StateUnknown;
        }
        else if (ChaosGate.IsCritical(run.Observation))
        {
            code = ChaosCauses.CriticalState;
        }
        else if (run.DataLossInvariantFailed)
        {
            code = ChaosCauses.DataLossInvariant;
        }
        else if (now < run.StartedAt)
        {
            code = ChaosCauses.ClockRegressed;
        }
        else if (run.OwnerAbortRequested)
        {
            code = ChaosCauses.OwnerAbort;
        }
        else if (now - run.StartedAt >= run.Experiment.Duration)
        {
            code = ChaosCauses.DurationExceeded;
        }

        return code is null
            ? new ChaosAbortDecision(false, ChaosCauses.Allowed, null)
            : new ChaosAbortDecision(true, code, now + deadline);
    }
}
