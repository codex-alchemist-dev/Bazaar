using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Chaos;

/// <summary>The fault kinds of the signed catalogue (doc 46 section 17.3). The catalogue itself and the concrete faults are not built.</summary>
public enum ChaosFault
{
    KillModule,
    SlowDependency,
    DropWireFrames,
    FillDisk,
    SkewClock,
    PauseGateway,
    CorruptSlotCopy,
}

/// <summary>Why a running experiment must stop (doc 46 section 17.7).</summary>
public enum ChaosAbortCondition
{
    /// <summary>An incident is open. Always enforced.</summary>
    IncidentFreeze,

    /// <summary>Any critical state. Always enforced.</summary>
    CriticalState,

    /// <summary>A data-loss invariant failed. Always enforced.</summary>
    DataLossInvariant,

    /// <summary>The person running it asked to stop.</summary>
    OwnerAbort,

    /// <summary>The declared duration ran out.</summary>
    DurationExceeded,
}

/// <summary>
/// One experiment: a fault, a hypothesis (the expected state transition and recovery time), a blast-radius limit, a bounded duration
/// and an abort deadline (section 17.3).
/// </summary>
/// <param name="Id">An identifier, unique in the plan.</param>
/// <param name="Fault">The fault from the catalogue.</param>
/// <param name="ExpectedState">The state the hypothesis expects the sandbox to reach.</param>
/// <param name="ExpectedRecovery">The recovery time the hypothesis expects.</param>
/// <param name="Duration">How long the fault is held.</param>
/// <param name="BlastRadiusPercent">How much of the sandbox the fault may touch, from 1 to 100 as declared (the settings cap it lower).</param>
/// <param name="AbortWithin">How fast an abort must stop it (at most 10 seconds).</param>
/// <param name="AbortConditions">The conditions the plan names. The always-enforced ones apply whether or not they are listed.</param>
public sealed record ChaosExperiment(
    string Id,
    ChaosFault Fault,
    KodoState ExpectedState,
    TimeSpan ExpectedRecovery,
    TimeSpan Duration,
    int BlastRadiusPercent,
    TimeSpan AbortWithin,
    IReadOnlyList<ChaosAbortCondition> AbortConditions);

/// <summary>A fault plan: experiments against one target.</summary>
/// <param name="Id">An identifier.</param>
/// <param name="TargetId">The target identifier. The gate classifies it and refuses all but a sandbox.</param>
/// <param name="Experiments">The experiments, in order.</param>
public sealed record ChaosPlan(string Id, string TargetId, IReadOnlyList<ChaosExperiment> Experiments);

/// <summary>Plan checks. Stable codes only.</summary>
public static class ChaosPlanValidator
{
    /// <summary>The codes for everything wrong with a plan against the settings, in a fixed order. Empty when the plan is acceptable.</summary>
    public static IReadOnlyList<string> Validate(ChaosPlan? plan, ChaosSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> codes = [];
        if (plan is null || !IsIdentifier(plan.Id) || plan.Experiments is null)
        {
            codes.Add(ChaosCauses.PlanInvalid);
            return codes;
        }

        if (plan.Experiments.Count == 0 || plan.Experiments.Count > settings.MaxExperiments)
        {
            codes.Add(ChaosCauses.PlanSize);
            return codes;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        TimeSpan total = TimeSpan.Zero;
        bool shape = false;
        bool duration = false;
        bool blast = false;
        bool abort = false;
        foreach (ChaosExperiment? experiment in plan.Experiments)
        {
            if (experiment is null || !IsIdentifier(experiment.Id) || !seen.Add(experiment.Id) || !Enum.IsDefined(experiment.Fault)
                || !Enum.IsDefined(experiment.ExpectedState) || experiment.ExpectedState == KodoState.Frozen
                || experiment.ExpectedRecovery <= TimeSpan.Zero || experiment.AbortConditions is null
                || experiment.AbortConditions.Any(c => !Enum.IsDefined(c)))
            {
                shape = true;
                continue;
            }

            duration |= experiment.Duration <= TimeSpan.Zero || experiment.Duration > settings.MaxExperimentDuration;
            blast |= experiment.BlastRadiusPercent < 1 || experiment.BlastRadiusPercent > settings.MaxBlastRadiusPercent;
            abort |= experiment.AbortWithin <= TimeSpan.Zero || experiment.AbortWithin > ChaosSettings.CeilingAbortWithin;
            total += experiment.Duration > TimeSpan.Zero ? experiment.Duration : TimeSpan.Zero;
        }

        if (shape)
        {
            codes.Add(ChaosCauses.ExperimentInvalid);
        }

        if (duration)
        {
            codes.Add(ChaosCauses.DurationExceeded);
        }

        if (total > settings.MaxTotalDuration)
        {
            codes.Add(ChaosCauses.TotalDurationExceeded);
        }

        if (blast)
        {
            codes.Add(ChaosCauses.BlastRadiusExceeded);
        }

        if (abort)
        {
            codes.Add(ChaosCauses.AbortDeadlineInvalid);
        }

        return codes;
    }

    private static bool IsIdentifier(string? value) => !string.IsNullOrEmpty(value) && KodoExportPatterns.IdentifierValue().IsMatch(value);
}
