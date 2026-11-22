using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;

namespace Bazaar.Kodo.Chaos;

/// <summary>Stable refusal and abort codes (doc 46 section 17.7).</summary>
public static class ChaosCauses
{
    public const string Allowed = "allowed";
    public const string Disabled = "chaos_disabled";
    public const string SettingsInvalid = "settings_invalid";
    public const string NoPermission = "permission_missing";
    public const string StepUpMissing = "step_up_missing";
    public const string StepUpStale = "step_up_stale";
    public const string IncidentOpen = "incident_open";
    public const string StateUnknown = "state_unknown";
    public const string CriticalState = "critical_state";
    public const string TargetNotSandbox = "target_not_sandbox";
    public const string PlanInvalid = "plan_invalid";
    public const string PlanSize = "plan_size";
    public const string ExperimentInvalid = "experiment_invalid";
    public const string DurationExceeded = "duration_exceeded";
    public const string TotalDurationExceeded = "total_duration_exceeded";
    public const string BlastRadiusExceeded = "blast_radius_exceeded";
    public const string AbortDeadlineInvalid = "abort_deadline_invalid";
    public const string DataLossInvariant = "data_loss_invariant";
    public const string OwnerAbort = "owner_abort";
    public const string ClockRegressed = "clock_regressed";
}

/// <summary>A request to start chaos. The caller (the API layer) fills the person's facts from the role and step-up services.</summary>
/// <param name="ActorId">Who asks.</param>
/// <param name="HasEnablePermission">The person holds <c>kodo.chaos.enable</c>.</param>
/// <param name="StepUpVerifiedAt">When the person last completed step-up, or null if not.</param>
/// <param name="Plan">The plan to run.</param>
public sealed record ChaosRequest(string ActorId, bool HasEnablePermission, DateTimeOffset? StepUpVerifiedAt, ChaosPlan Plan);

/// <summary>The gate's answer: allowed, or every reason it refused, in a fixed order.</summary>
/// <param name="Allowed">Whether chaos may start.</param>
/// <param name="Codes">One <see cref="ChaosCauses.Allowed"/> code, or the refusal codes.</param>
public sealed record ChaosDecision(bool Allowed, IReadOnlyList<string> Codes);

/// <summary>
/// The chaos gate (doc 46 sections 17.3 and 17.7). Pure and fail-closed: anything missing, unknown or invalid refuses. It collects every
/// reason rather than stopping at the first, so the audit record is complete.
/// </summary>
public static class ChaosGate
{
    /// <summary>True for any state that must stop chaos: critical red or unknown, integrity or contract failure, crash loop, or an active incident.</summary>
    public static bool IsCritical(KodoObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.CriticalRed || observation.CriticalUnknown || observation.IntegrityOrContractFailure || observation.CrashLoop || observation.IncidentActive;
    }

    /// <summary>Decides whether chaos may start.</summary>
    /// <param name="settings">The chaos settings.</param>
    /// <param name="request">What is asked.</param>
    /// <param name="incident">The open incident, or null. Any open incident refuses (the freeze blocks <see cref="FrozenAction.ChaosEnable"/>).</param>
    /// <param name="observation">The current observation, or null when it is not known. Null refuses.</param>
    /// <param name="now">The current time. Passed in; no clock is read.</param>
    public static ChaosDecision Evaluate(ChaosSettings settings, ChaosRequest? request, IncidentRecord? incident, KodoObservation? observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> codes = [];
        if (!settings.Enabled)
        {
            return Refuse(ChaosCauses.Disabled);
        }

        if (!settings.IsValid())
        {
            return Refuse(ChaosCauses.SettingsInvalid);
        }

        if (request is null)
        {
            return Refuse(ChaosCauses.PlanInvalid);
        }

        if (!request.HasEnablePermission)
        {
            codes.Add(ChaosCauses.NoPermission);
        }

        if (request.StepUpVerifiedAt is not { } stepUp)
        {
            codes.Add(ChaosCauses.StepUpMissing);
        }
        else if (stepUp > now || now - stepUp > settings.MaxStepUpAge)
        {
            codes.Add(ChaosCauses.StepUpStale);
        }

        if (IncidentFreeze.IsFrozen(incident))
        {
            codes.Add(ChaosCauses.IncidentOpen);
        }

        if (observation is null)
        {
            codes.Add(ChaosCauses.StateUnknown);
        }
        else if (IsCritical(observation))
        {
            codes.Add(ChaosCauses.CriticalState);
        }

        if (ChaosTargetClassifier.Classify(request.Plan?.TargetId, settings.KnownProductionIds) != ChaosTargetClass.Sandbox)
        {
            codes.Add(ChaosCauses.TargetNotSandbox);
        }

        codes.AddRange(ChaosPlanValidator.Validate(request.Plan, settings));
        return codes.Count == 0 ? new ChaosDecision(true, [ChaosCauses.Allowed]) : new ChaosDecision(false, codes);

        static ChaosDecision Refuse(string code) => new(false, [code]);
    }
}
