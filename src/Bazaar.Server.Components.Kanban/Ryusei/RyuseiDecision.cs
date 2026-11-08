namespace Bazaar.Server.Kanban.Ryusei;

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

    /// <summary>The agent already has its maximum number of active triggers.</summary>
    ActiveTriggerCapReached,

    /// <summary>A trigger was given no ping targets.</summary>
    NoTargets,

    /// <summary>A trigger was given more ping targets than the fan-out cap allows.</summary>
    PingFanoutExceeded,
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
