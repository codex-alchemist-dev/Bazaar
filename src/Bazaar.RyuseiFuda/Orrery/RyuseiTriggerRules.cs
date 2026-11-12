namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>Who is acting. An agent acts within its own project; the owner acts on any trigger (doc 38, Authority).</summary>
public enum RyuseiActorKind
{
    Owner,
    Agent,
}

/// <summary>The actor of a trigger change. <see cref="ProjectId"/> is set for agents and null for the owner.</summary>
public readonly record struct RyuseiActor(RyuseiActorKind Kind, Guid Id, Guid? ProjectId)
{
    public static RyuseiActor ForOwner(Guid ownerId) => new(RyuseiActorKind.Owner, ownerId, null);

    public static RyuseiActor ForAgent(Guid agentId, Guid projectId) => new(RyuseiActorKind.Agent, agentId, projectId);

    /// <summary>The text stored in the audit actor column, in the same form as <c>TaskActor.ToText</c>.</summary>
    public string ToAuditText() => Kind == RyuseiActorKind.Owner ? $"owner:{Id:D}" : $"agent:{Id:D}";
}

/// <summary>
/// One ping target as the caller resolved it from the agent directory. The rules need the project and whether the agent
/// is active (not resting or retired) at the moment of creation.
/// </summary>
public sealed record RyuseiTargetInfo(Guid AgentId, Guid ProjectId, bool IsActive);

/// <summary>The <c>ryusei.coworker_triggers</c> setting (doc 38, Authority). Default is <see cref="Allowed"/>.</summary>
public enum RyuseiCoworkerTriggers
{
    Allowed,

    /// <summary>Refused until an approval flow exists. No approval flow is built yet.</summary>
    AskFirst,

    Off,
}

/// <summary>
/// The configured caps the creation rules read (doc 38, Loop and runaway safeguards). The guards clamp each value to its
/// documented range, so an out-of-range setting cannot loosen a guard past its floor.
/// </summary>
public sealed record RyuseiLimits
{
    /// <summary>ryusei.min_interval. The effective floor is the larger of this and the hard floor of one minute.</summary>
    public TimeSpan MinInterval { get; init; } = RyuseiDefaults.MinInterval;

    /// <summary>ryusei.max_triggers_per_agent: active and paused triggers per target agent.</summary>
    public int MaxTriggersPerAgent { get; init; } = RyuseiDefaults.MaxTriggersPerAgent;

    /// <summary>ryusei.max_chain_depth, 1 to 3.</summary>
    public int MaxChainDepth { get; init; } = RyuseiDefaults.MaxChainDepth;

    /// <summary>ryusei.max_ping_fanout, 1 to 8.</summary>
    public int MaxPingFanout { get; init; } = RyuseiDefaults.MaxPingFanout;

    /// <summary>ryusei.max_fires_per_day: woke fires per trigger in any 24 hours. The tick reads it (doc 38, guard table).</summary>
    public int MaxFiresPerDay { get; init; } = RyuseiDefaults.MaxFiresPerDay;

    /// <summary>runtime.guard.wakes_per_hour: wakes per target agent per hour, all reasons. Floor none (doc 38, guard table).</summary>
    public int MaxAgentWakesPerHour { get; init; } = RyuseiDefaults.MaxAgentWakesPerHour;

    /// <summary>ryusei.cooldown_seconds: the gap a trigger keeps between wakes. The guard floors it at 10 seconds.</summary>
    public int CooldownSeconds { get; init; } = RyuseiDefaults.CooldownSeconds;

    /// <summary>ryusei.auto_pause_after: consecutive refusals before a trigger pauses, 1 to 20.</summary>
    public int AutoPauseAfter { get; init; } = RyuseiDefaults.AutoPauseAfter;
}

/// <summary>A creation request as the pure rules see it. Nothing here touches the database.</summary>
public sealed record RyuseiCreateRequest(
    RyuseiActor Creator,
    Guid ProjectId,
    IReadOnlyList<RyuseiTargetInfo> Targets,
    RyuseiTriggerKind Kind,
    RyuseiTriggerSpec Spec,
    string Purpose,
    Guid? AttachGcId,
    RyuseiPingMode PingMode,
    int ChainDepth,
    RyuseiCoworkerTriggers CoworkerTriggers);

/// <summary>A rule outcome. <see cref="Refusal"/> is <see cref="RyuseiRefusal.None"/> when the rule allows the action.</summary>
public sealed record RyuseiCheck(RyuseiRefusal Refusal, string Message)
{
    public static RyuseiCheck Allow { get; } = new(RyuseiRefusal.None, string.Empty);

    public bool IsAllowed => Refusal == RyuseiRefusal.None;

    public static RyuseiCheck Refuse(RyuseiRefusal refusal, string message)
    {
        if (refusal == RyuseiRefusal.None)
        {
            throw new ArgumentOutOfRangeException(nameof(refusal), "A refusal needs a reason.");
        }

        return new RyuseiCheck(refusal, message);
    }
}

/// <summary>
/// The authority matrix and the creation rules as pure functions (doc 38, Authority and Loop and runaway safeguards). The
/// service calls these; the tests call them directly.
/// </summary>
public static class RyuseiTriggerRules
{
    /// <summary>
    /// Every check that needs no database. Order: shape of the request, then ping and chain limits, then the spec, then
    /// authority. The active-trigger cap is separate (<see cref="CheckActiveCaps"/>) because it needs the live counts.
    /// </summary>
    public static RyuseiCheck CheckCreate(RyuseiCreateRequest request, RyuseiLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(request.Targets);
        ArgumentNullException.ThrowIfNull(request.Spec);
        if (request.ChainDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.ChainDepth, "Chain depth cannot be negative.");
        }

        if (request.Spec.Kind != request.Kind)
        {
            return RyuseiCheck.Refuse(RyuseiRefusal.KindMismatch, $"The trigger kind {request.Kind} does not match its spec, which is {request.Spec.Kind}.");
        }

        if (string.IsNullOrWhiteSpace(request.Purpose))
        {
            return RyuseiCheck.Refuse(RyuseiRefusal.InvalidPurpose, "A trigger needs a purpose.");
        }

        if (request.PingMode == RyuseiPingMode.Attach && request.AttachGcId is null)
        {
            return RyuseiCheck.Refuse(RyuseiRefusal.PingModeMismatch, "Attach ping mode needs an attach GC.");
        }

        if (request.PingMode != RyuseiPingMode.Attach && request.AttachGcId is not null)
        {
            return RyuseiCheck.Refuse(RyuseiRefusal.PingModeMismatch, $"Ping mode {request.PingMode} does not take an attach GC.");
        }

        var fanout = RyuseiGuards.CheckPingFanout(request.Targets.Count, limits.MaxPingFanout);
        if (!fanout.IsAllowed)
        {
            var message = fanout.Refusal == RyuseiRefusal.NoTargets
                ? "A trigger needs at least one target."
                : $"A trigger may name at most {Math.Clamp(limits.MaxPingFanout, 1, RyuseiDefaults.MaxPingFanout)} targets; it named {request.Targets.Count}.";
            return RyuseiCheck.Refuse(fanout.Refusal, message);
        }

        if (request.Targets.Select(t => t.AgentId).Distinct().Count() != request.Targets.Count)
        {
            return RyuseiCheck.Refuse(RyuseiRefusal.DuplicateTarget, "A trigger may name each target agent only once.");
        }

        var depth = RyuseiGuards.CheckChainDepth(request.ChainDepth, limits.MaxChainDepth);
        if (!depth.IsAllowed)
        {
            return RyuseiCheck.Refuse(depth.Refusal, $"Chain depth {request.ChainDepth} is above the maximum of {Math.Clamp(limits.MaxChainDepth, 1, RyuseiDefaults.MaxChainDepth)}.");
        }

        var specCheck = CheckSpec(request.Spec, limits);
        if (!specCheck.IsAllowed)
        {
            return specCheck;
        }

        return RyuseiAuthority.CheckCreation(request.Creator, request.ProjectId, request.Targets, request.CoworkerTriggers);
    }

    /// <summary>
    /// The active-trigger cap per target. <paramref name="liveTriggersByAgent"/> counts the triggers already active or paused
    /// for each agent. Paused triggers count too, so pausing cannot be used to get past the cap. Agents missing from the
    /// dictionary have zero.
    /// </summary>
    public static RyuseiCheck CheckActiveCaps(
        IEnumerable<Guid> targetAgentIds,
        IReadOnlyDictionary<Guid, int> liveTriggersByAgent,
        int maxTriggersPerAgent)
    {
        ArgumentNullException.ThrowIfNull(targetAgentIds);
        ArgumentNullException.ThrowIfNull(liveTriggersByAgent);

        foreach (var agentId in targetAgentIds)
        {
            var live = liveTriggersByAgent.GetValueOrDefault(agentId);
            var decision = RyuseiGuards.CheckActiveTriggerCap(live, maxTriggersPerAgent);
            if (!decision.IsAllowed)
            {
                return RyuseiCheck.Refuse(
                    decision.Refusal,
                    $"Agent {agentId:D} already has {live} active or paused triggers; the limit is {maxTriggersPerAgent}.");
            }
        }

        return RyuseiCheck.Allow;
    }

    private static RyuseiCheck CheckSpec(RyuseiTriggerSpec spec, RyuseiLimits limits)
    {
        switch (spec)
        {
            case RyuseiIntervalTriggerSpec interval:
                {
                    var floor = RyuseiSchedule.CheckIntervalFloor(interval.Every, limits.MinInterval);
                    return floor.IsAllowed
                        ? RyuseiCheck.Allow
                        : RyuseiCheck.Refuse(
                            floor.Refusal,
                            $"Interval {interval.Every} is below the floor of {RyuseiSchedule.EffectiveIntervalFloor(limits.MinInterval)}.");
                }

            case RyuseiWatcherTriggerSpec watcher:
                return CheckPredicate(watcher.Predicate);

            case RyuseiEventTriggerSpec evt:
                return CheckPredicate(evt.Predicate);

            default:
                return RyuseiCheck.Allow;
        }
    }

    private static RyuseiCheck CheckPredicate(RyuseiPredicate predicate)
    {
        var validation = RyuseiPredicateValidator.Validate(predicate);
        return validation.IsValid
            ? RyuseiCheck.Allow
            : RyuseiCheck.Refuse(RyuseiRefusal.InvalidPredicate, validation.Error!.Message);
    }
}

/// <summary>
/// The authority matrix (doc 38, Authority) as pure functions. Creation rules come first; a pause, resume or cancel is
/// checked against the trigger's stored creator and targets.
/// </summary>
public static class RyuseiAuthority
{
    /// <summary>
    /// Who may create a trigger that targets these agents. The owner may target any agent in the project. An agent may
    /// target itself, may target a coworker in its own project when <paramref name="coworker"/> allows it, and may not
    /// target another project or an agent that is resting or retired.
    /// </summary>
    public static RyuseiCheck CheckCreation(
        RyuseiActor creator,
        Guid projectId,
        IReadOnlyList<RyuseiTargetInfo> targets,
        RyuseiCoworkerTriggers coworker)
    {
        ArgumentNullException.ThrowIfNull(targets);

        switch (creator.Kind)
        {
            case RyuseiActorKind.Owner:
                foreach (var target in targets)
                {
                    if (target.ProjectId != projectId)
                    {
                        return RyuseiCheck.Refuse(RyuseiRefusal.TargetOutsideProject, $"Agent {target.AgentId:D} is not in project {projectId:D}.");
                    }
                }

                return RyuseiCheck.Allow;

            case RyuseiActorKind.Agent:
                if (creator.ProjectId != projectId)
                {
                    return RyuseiCheck.Refuse(RyuseiRefusal.TargetOutsideProject, "The creating agent is not in this project.");
                }

                foreach (var target in targets)
                {
                    if (target.AgentId == creator.Id)
                    {
                        continue;
                    }

                    if (target.ProjectId != projectId)
                    {
                        return RyuseiCheck.Refuse(RyuseiRefusal.TargetOutsideProject, $"Agent {target.AgentId:D} is in another project.");
                    }

                    if (!target.IsActive)
                    {
                        return RyuseiCheck.Refuse(RyuseiRefusal.TargetNotActive, $"Agent {target.AgentId:D} is resting or retired.");
                    }

                    switch (coworker)
                    {
                        case RyuseiCoworkerTriggers.Off:
                            return RyuseiCheck.Refuse(RyuseiRefusal.CoworkerTriggersOff, "Coworker triggers are turned off for this instance.");
                        case RyuseiCoworkerTriggers.AskFirst:
                            return RyuseiCheck.Refuse(RyuseiRefusal.CoworkerTriggersNeedApproval, "Coworker triggers need the owner's approval on this instance.");
                    }
                }

                return RyuseiCheck.Allow;

            default:
                return RyuseiCheck.Refuse(RyuseiRefusal.NotAuthorized, "Unknown actor.");
        }
    }

    /// <summary>Pause and resume: the owner, or the target agent. Pausing is never refused for the target.</summary>
    public static RyuseiCheck CheckPauseOrResume(RyuseiActor actor, IReadOnlyCollection<Guid> targetAgentIds)
    {
        ArgumentNullException.ThrowIfNull(targetAgentIds);
        if (actor.Kind == RyuseiActorKind.Owner)
        {
            return RyuseiCheck.Allow;
        }

        return actor.Kind == RyuseiActorKind.Agent && targetAgentIds.Contains(actor.Id)
            ? RyuseiCheck.Allow
            : RyuseiCheck.Refuse(RyuseiRefusal.NotAuthorized, "Only the target agent or the owner may pause or resume this trigger.");
    }

    /// <summary>Cancel: the creator, or the owner.</summary>
    public static RyuseiCheck CheckCancel(RyuseiActor actor, Guid createdBy)
    {
        if (actor.Kind == RyuseiActorKind.Owner)
        {
            return RyuseiCheck.Allow;
        }

        return actor.Kind == RyuseiActorKind.Agent && actor.Id == createdBy
            ? RyuseiCheck.Allow
            : RyuseiCheck.Refuse(RyuseiRefusal.NotAuthorized, "Only the creator or the owner may cancel this trigger.");
    }
}
