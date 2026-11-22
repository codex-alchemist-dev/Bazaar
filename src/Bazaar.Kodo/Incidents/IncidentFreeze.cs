using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Incidents;

/// <summary>What the freeze may block (doc 46 section 16.3 item 2).</summary>
public enum FrozenAction
{
    /// <summary>Reads, planning and viewing. Never blocked.</summary>
    ReadOnly,

    /// <summary>An automatic update (doc 49).</summary>
    AutomaticUpdate,

    /// <summary>A configuration change. Allowed only for keys on the incident allowlist.</summary>
    ConfigChange,

    /// <summary>An agent's high-impact action (doc 15's kill switch scope <c>incident</c>).</summary>
    AgentHighImpact,

    /// <summary>A person starts a runbook. Allowed only for the incident's own runbooks.</summary>
    RunbookStart,

    /// <summary>A runbook starts without a person. Never allowed during an incident.</summary>
    AutomaticRunbook,

    /// <summary>A module install.</summary>
    ModuleInstall,

    /// <summary>Chaos mode (section 17.3).</summary>
    ChaosEnable,
}

/// <summary>The answer of <see cref="IncidentFreeze.Check"/>.</summary>
/// <param name="Allowed">Whether the action may go ahead.</param>
/// <param name="Code">A fixed code: <c>no_incident</c>, <c>read_only</c>, <c>allowlisted</c>, <c>incident_runbook</c>, or a refusal code.</param>
public sealed record FreezeDecision(bool Allowed, string Code);

/// <summary>
/// The freeze rules (section 16.3 item 2 and section 8.1). While an incident is open (every phase but closed) the evaluator shows
/// <see cref="KodoState.Frozen"/>, automatic actions pause, and nothing lifts the freeze except a confirmed exit.
/// </summary>
public static class IncidentFreeze
{
    /// <summary>The scope name for doc 15's kill switch.</summary>
    public const string KillSwitchScope = "incident";

    /// <summary>True while the incident holds the freeze: every phase until it is closed.</summary>
    public static bool IsFrozen(IncidentRecord? incident) => incident is { IsActive: true };

    /// <summary>The observation with incident mode set from the record. The evaluator then shows <see cref="KodoState.Frozen"/> beside the underlying state.</summary>
    public static KodoObservation Observe(KodoObservation observation, IncidentRecord? incident)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation with { IncidentActive = IsFrozen(incident) };
    }

    /// <summary>Whether an action may proceed. Anything unknown is refused while frozen.</summary>
    /// <param name="incident">The open incident, or null.</param>
    /// <param name="action">What is being attempted.</param>
    /// <param name="subject">The config key or runbook id the action concerns, when it has one.</param>
    /// <param name="options">The incident settings (for the config allowlist).</param>
    public static FreezeDecision Check(IncidentRecord? incident, FrozenAction action, string? subject, IncidentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsFrozen(incident))
        {
            return new FreezeDecision(true, "no_incident");
        }

        return action switch
        {
            FrozenAction.ReadOnly => new FreezeDecision(true, "read_only"),
            FrozenAction.ConfigChange when subject is not null && options.ConfigAllowlist.Contains(subject) => new FreezeDecision(true, "allowlisted"),
            FrozenAction.RunbookStart when subject is not null && incident!.Plan.Any(p => string.Equals(p.RunbookId, subject, StringComparison.Ordinal)) =>
                new FreezeDecision(true, "incident_runbook"),
            FrozenAction.RunbookStart => new FreezeDecision(false, "runbook_not_in_incident_plan"),
            FrozenAction.AutomaticRunbook => new FreezeDecision(false, "automatic_runbook_frozen"),
            _ => new FreezeDecision(false, "incident_frozen"),
        };
    }
}
