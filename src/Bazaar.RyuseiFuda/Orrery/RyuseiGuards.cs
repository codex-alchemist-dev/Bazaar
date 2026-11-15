namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// The loop and runaway guards as pure arithmetic (doc 38, Loop and runaway safeguards). Each takes the configured
/// value as a parameter. Configured values are clamped to the range doc 38 gives for each setting, so a setting can
/// tighten a guard but never loosen it past its floor. Each guard returns a <see cref="RyuseiDecision"/>.
/// </summary>
public static class RyuseiGuards
{
    /// <summary>
    /// Chain depth: a trigger created from a triggered turn has depth parent + 1. Allowed while depth is at or below the
    /// maximum. The setting is clamped to 1 to 3.
    /// </summary>
    public static RyuseiDecision CheckChainDepth(int chainDepth, int maxChainDepth)
    {
        var effective = Math.Clamp(maxChainDepth, 1, RyuseiDefaults.MaxChainDepth);
        return chainDepth <= effective ? RyuseiDecision.Allow : RyuseiDecision.Refuse(RyuseiRefusal.ChainDepthExceeded);
    }

    /// <summary>
    /// Cooldown between fires of one trigger. The fire is allowed once at least the cooldown has passed since
    /// <paramref name="lastFiredAt"/>. A trigger that has never fired passes null. The setting has a floor of 10 seconds.
    /// </summary>
    public static RyuseiDecision CheckCooldown(DateTimeOffset now, DateTimeOffset? lastFiredAt, int cooldownSeconds)
    {
        if (lastFiredAt is null)
        {
            return RyuseiDecision.Allow;
        }

        var effective = TimeSpan.FromSeconds(Math.Max(cooldownSeconds, RyuseiDefaults.CooldownFloorSeconds));
        return now - lastFiredAt.Value < effective
            ? RyuseiDecision.Refuse(RyuseiRefusal.CooldownActive)
            : RyuseiDecision.Allow;
    }

    /// <summary>
    /// Single flight: one fire in flight per trigger (doc 38). <paramref name="fireInFlight"/> is true when another fire of the same
    /// trigger is still being written, so this one must not start. Refusing it leaves the schedule where it is.
    /// </summary>
    public static RyuseiDecision CheckSingleFlight(bool fireInFlight) =>
        fireInFlight ? RyuseiDecision.Refuse(RyuseiRefusal.SingleFlightHeld) : RyuseiDecision.Allow;

    /// <summary>
    /// Fires per trigger per 24 hours. <paramref name="firesInLast24Hours"/> counts the fires already made, not the one
    /// being decided, so with a cap of 96 the 96th fire is allowed (95 prior) and the 97th is refused (96 prior).
    /// </summary>
    public static RyuseiDecision CheckDailyFireCap(int firesInLast24Hours, int maxFiresPerDay) =>
        firesInLast24Hours < maxFiresPerDay ? RyuseiDecision.Allow : RyuseiDecision.Refuse(RyuseiRefusal.DailyFireCapReached);

    /// <summary>
    /// Auto-pause. <paramref name="consecutiveRefusals"/> includes the refusal just recorded. A refusal at or past the
    /// threshold trips the pause. The setting is clamped to 1 to 20. A refusal here means the trigger must be paused.
    /// </summary>
    public static RyuseiDecision CheckRefusalStreak(int consecutiveRefusals, int autoPauseAfter)
    {
        var effective = Math.Clamp(autoPauseAfter, 1, 20);
        return consecutiveRefusals >= effective
            ? RyuseiDecision.Refuse(RyuseiRefusal.AutoPauseTripped)
            : RyuseiDecision.Allow;
    }

    /// <summary>
    /// Wakes per agent per hour, shared with doc 06 and counted across all wake reasons. <paramref name="wakesInLastHour"/>
    /// counts the wakes already made, not the one being decided. Refusing does not pause the trigger.
    /// </summary>
    public static RyuseiDecision CheckAgentWakeCap(int wakesInLastHour, int maxWakesPerHour) =>
        wakesInLastHour < maxWakesPerHour ? RyuseiDecision.Allow : RyuseiDecision.Refuse(RyuseiRefusal.AgentWakeCapReached);

    /// <summary>
    /// The guards every wake passes, in this fixed order: cooldown, then the daily fire cap, then the shared wake cap. The first
    /// refusal found decides the outcome. The scheduler tick and the event bridge both call it, so an event wake meets the same
    /// limits as a timed one (doc 38, WP-R6).
    /// </summary>
    public static RyuseiDecision CheckWake(
        DateTimeOffset now,
        DateTimeOffset? lastWokeAt,
        int firesInLast24Hours,
        int wakesInLastHour,
        RyuseiLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var cooldown = CheckCooldown(now, lastWokeAt, limits.CooldownSeconds);
        var guard = cooldown.IsAllowed ? CheckDailyFireCap(firesInLast24Hours, limits.MaxFiresPerDay) : cooldown;
        if (guard.IsAllowed)
        {
            guard = CheckAgentWakeCap(wakesInLastHour, limits.MaxAgentWakesPerHour);
        }

        return guard;
    }

    /// <summary>
    /// Active triggers per agent. <paramref name="activeTriggers"/> counts the ones already active, not the one being
    /// created. So with a cap of 50 the 50th is allowed (49 active) and the 51st is refused (50 active).
    /// </summary>
    public static RyuseiDecision CheckActiveTriggerCap(int activeTriggers, int maxTriggersPerAgent) =>
        activeTriggers < maxTriggersPerAgent ? RyuseiDecision.Allow : RyuseiDecision.Refuse(RyuseiRefusal.ActiveTriggerCapReached);

    /// <summary>
    /// Ping fan-out: a trigger names 1 to the cap of targets. The cap is clamped to 1 to 8. A trigger with more targets
    /// is refused at creation, not at fire time.
    /// </summary>
    public static RyuseiDecision CheckPingFanout(int targetCount, int maxPingFanout)
    {
        if (targetCount < 1)
        {
            return RyuseiDecision.Refuse(RyuseiRefusal.NoTargets);
        }

        var effective = Math.Clamp(maxPingFanout, 1, RyuseiDefaults.MaxPingFanout);
        return targetCount <= effective ? RyuseiDecision.Allow : RyuseiDecision.Refuse(RyuseiRefusal.PingFanoutExceeded);
    }
}
