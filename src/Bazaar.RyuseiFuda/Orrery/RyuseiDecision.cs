namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>Why a RyuseiFuda guard refused. <see cref="None"/> means the guard allowed the action.</summary>
public enum RyuseiRefusal
{
    None,

    /// <summary>An <c>interval</c> spec is shorter than the effective floor (doc 38, Loop and runaway safeguards).</summary>
    IntervalBelowFloor,

    /// <summary>The trigger would be deeper in a chain than the allowed maximum.</summary>
    ChainDepthExceeded,

    /// <summary>The last fire of this trigger is more recent than the cooldown.</summary>
    CooldownActive,

    /// <summary>The trigger has already fired its allowed number of times in the last 24 hours.</summary>
    DailyFireCapReached,

    /// <summary>Consecutive refusals reached the threshold. The trigger must be paused and the owner notified.</summary>
    AutoPauseTripped,

    /// <summary>
    /// The target agent already has its maximum number of wakes in the last hour, counted across all reasons (doc 38, guard
    /// table; shared with doc 06). The fire is recorded as refused and the trigger is not paused, so it is not lost.
    /// </summary>
    AgentWakeCapReached,

    /// <summary>The agent already has its maximum number of active triggers.</summary>
    ActiveTriggerCapReached,

    /// <summary>A trigger was given no ping targets.</summary>
    NoTargets,

    /// <summary>A trigger was given more ping targets than the fan-out cap allows.</summary>
    PingFanoutExceeded,

    /// <summary>A watcher or event predicate failed <see cref="RyuseiPredicateValidator"/>. The message says why.</summary>
    InvalidPredicate,

    /// <summary>The trigger kind does not match the spec's kind.</summary>
    KindMismatch,

    /// <summary>The purpose is empty or only whitespace.</summary>
    InvalidPurpose,

    /// <summary>The ping mode and the attach GC disagree. Attach needs a GC; none and new_gc forbid one.</summary>
    PingModeMismatch,

    /// <summary>The same agent was named twice in one trigger's targets.</summary>
    DuplicateTarget,

    /// <summary>An agent creator tried to target an agent in another project (doc 38, Authority).</summary>
    TargetOutsideProject,

    /// <summary>An agent creator tried to target a resting or retired agent (doc 38, Authority).</summary>
    TargetNotActive,

    /// <summary>A coworker trigger was refused because <c>ryusei.coworker_triggers</c> is <c>off</c>.</summary>
    CoworkerTriggersOff,

    /// <summary>A coworker trigger was refused because <c>ryusei.coworker_triggers</c> is <c>ask_first</c>. No approval flow exists yet.</summary>
    CoworkerTriggersNeedApproval,

    /// <summary>The actor is not allowed to make this change (doc 38, Authority).</summary>
    NotAuthorized,

    /// <summary>
    /// A fire for this trigger is already in flight, so this fire is skipped (doc 38, single flight). The claim's row lock is what
    /// keeps two ticks from firing one trigger; this refusal is the recorded form of that rule.
    /// </summary>
    SingleFlightHeld,
}

/// <summary>
/// The result of a RyuseiFuda guard. <see cref="IsAllowed"/> is true only when <see cref="Refusal"/> is
/// <see cref="RyuseiRefusal.None"/>. The guards are pure: the caller records the refusal (doc 38, fire outcomes).
/// </summary>
public readonly record struct RyuseiDecision(RyuseiRefusal Refusal)
{
    public static RyuseiDecision Allow { get; } = new(RyuseiRefusal.None);

    public bool IsAllowed => Refusal == RyuseiRefusal.None;

    public static RyuseiDecision Refuse(RyuseiRefusal refusal)
    {
        if (refusal == RyuseiRefusal.None)
        {
            throw new ArgumentOutOfRangeException(nameof(refusal), "A refusal needs a reason.");
        }

        return new RyuseiDecision(refusal);
    }
}
