using System.Text.Json.Nodes;
using Bazaar.Server.Agents.Inbox;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>What the tick sees about a target agent when it decides a fire (doc 38, Behaviour and edge cases).</summary>
public enum RyuseiFireTargetStatus
{
    /// <summary>Created, onboarding or active. The wake is enqueued.</summary>
    Wakeable,

    /// <summary>Resting. The fire is recorded as skipped and does not wait.</summary>
    Resting,

    /// <summary>Retired. The fire is recorded as skipped.</summary>
    Retired,

    /// <summary>No <c>agent</c> row has this id.</summary>
    Missing,
}

/// <summary>One target of a trigger, with its status at fire time.</summary>
public sealed record RyuseiFireTarget(Guid AgentId, RyuseiFireTargetStatus Status);

/// <summary>The outcome of one fire, as stored in <c>ryusei_fire.outcome</c>. Each maps to one 0001 CHECK value.</summary>
public enum RyuseiFireOutcome
{
    /// <summary>The target agents were woken: one inbox item each, keyed by the fire id.</summary>
    Woke,

    /// <summary>The per-trigger daily fire cap was reached. The trigger is paused and the owner is told.</summary>
    RefusedCap,

    /// <summary>The last wake is more recent than the cooldown.</summary>
    RefusedCooldown,

    /// <summary>Every target was resting or retired, so nobody was woken.</summary>
    SkippedResting,

    /// <summary>The trigger cannot be fired as stored (malformed spec, or an interval below the floor). The trigger is paused.</summary>
    Error,
}

/// <summary>What the tick reads for one due trigger. The rules take it as a value; nothing is read from a clock or database here.</summary>
/// <param name="Trigger">The locked row as it is stored now.</param>
/// <param name="Spec">The decoded spec.</param>
/// <param name="Zone">The zone that anchors an interval's wall-clock days (doc 09).</param>
/// <param name="Targets">Each target agent and its status.</param>
/// <param name="FiresInLast24Hours">Woke fires of this trigger in the last 24 hours.</param>
/// <param name="LastWokeAt">The time of the latest woke fire, or null when there is none.</param>
public sealed record RyuseiTickInput(
    RyuseiTriggerRecord Trigger,
    RyuseiTriggerSpec Spec,
    TimeZoneInfo Zone,
    IReadOnlyList<RyuseiFireTarget> Targets,
    int FiresInLast24Hours,
    DateTimeOffset? LastWokeAt);

/// <summary>
/// The result for one due trigger: the fire row to insert, and how the trigger row changes. <see cref="WakeAgentIds"/> is
/// empty unless <see cref="Outcome"/> is <see cref="RyuseiFireOutcome.Woke"/>.
/// </summary>
public sealed record RyuseiFireDecision(
    RyuseiFireOutcome Outcome,
    DateTimeOffset ScheduledFor,
    RyuseiTriggerState NextState,
    DateTimeOffset? NextFireAt,
    int ConsecutiveRefusals,
    IReadOnlyList<Guid> WakeAgentIds,
    string? WakeReason,
    JsonObject Cause)
{
    /// <summary>True when the fire woke its targets and so counts toward the fire count and the daily cap.</summary>
    public bool Woke => Outcome == RyuseiFireOutcome.Woke;
}

/// <summary>
/// Which due triggers fire at an instant, and how they change (doc 38, Layer 2 firing and Loop and runaway safeguards).
/// Pure: no clock, no database. The tick service reads the rows, calls <see cref="Decide"/> and writes what it returns.
/// </summary>
/// <remarks>
/// Decisions, in order: a trigger that is not active or not due is left alone (null). A fire whose targets are all
/// resting or retired is skipped. A fire inside the cooldown or past the daily cap is refused. Otherwise the fire wakes
/// every wakeable target. A refusal counts toward auto-pause. Every fire that is decided, woke or refused, moves the
/// schedule on, so a refused occurrence is never retried at the same scheduled time.
/// </remarks>
public static class RyuseiTickRules
{
    /// <summary>
    /// The kinds the tick fires. Watchers and events need predicate evaluation over the event stream (WP-R6), so the claim
    /// and the decision both leave them alone until that exists.
    /// </summary>
    public static bool IsTickable(RyuseiTriggerKind kind) => kind is RyuseiTriggerKind.Timer or RyuseiTriggerKind.Interval;

    /// <summary>The inbox wake kind for a trigger kind (doc 38, Wake reasons).</summary>
    public static string WakeReason(RyuseiTriggerKind kind) => kind switch
    {
        RyuseiTriggerKind.Timer => InboxKind.RyuseiTimer,
        RyuseiTriggerKind.Interval => InboxKind.RyuseiInterval,
        RyuseiTriggerKind.Watcher => InboxKind.RyuseiWatcher,
        RyuseiTriggerKind.Event => InboxKind.RyuseiEvent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a trigger kind."),
    };

    /// <summary>The text stored in <c>ryusei_fire.outcome</c>.</summary>
    public static string ToSql(RyuseiFireOutcome outcome) => outcome switch
    {
        RyuseiFireOutcome.Woke => "woke",
        RyuseiFireOutcome.RefusedCap => "refused_cap",
        RyuseiFireOutcome.RefusedCooldown => "refused_cooldown",
        RyuseiFireOutcome.SkippedResting => "skipped_resting",
        RyuseiFireOutcome.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a fire outcome."),
    };

    /// <summary>
    /// Decides one due trigger at <paramref name="now"/>. Returns null when the trigger is not active, is not a tickable
    /// kind, or is not due: the caller then writes nothing for it.
    /// </summary>
    public static RyuseiFireDecision? Decide(RyuseiTickInput input, DateTimeOffset now, RyuseiLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);

        var trigger = input.Trigger;
        if (trigger.State != RyuseiTriggerState.Active || !IsTickable(trigger.Kind))
        {
            return null;
        }

        if (trigger.NextFireAt is not { } nextFireAt || nextFireAt > now)
        {
            return null;
        }

        if (input.Spec.Kind != trigger.Kind)
        {
            return Malformed(trigger, "The stored spec is for a different trigger kind.");
        }

        var isTimer = trigger.Kind == RyuseiTriggerKind.Timer;
        DateTimeOffset scheduledFor;
        DateTimeOffset? nextRunAt = null;
        long missedCount = 0;
        switch (input.Spec)
        {
            case RyuseiTimerTriggerSpec timer:
                scheduledFor = timer.FireAt;
                break;

            case RyuseiIntervalTriggerSpec interval:
                if (!RyuseiSchedule.CheckIntervalFloor(interval.Every, limits.MinInterval).IsAllowed)
                {
                    return Malformed(trigger, "The stored interval is below the effective floor.");
                }

                // A trigger that has never fired has no handled occurrence, so every due one coalesces from the anchor. After a
                // fire, the stored next_fire_at is the first unhandled occurrence, so the occurrence just before it is the last handled.
                var lastHandled = trigger.FireCount == 0 ? (DateTimeOffset?)null : nextFireAt - TimeSpan.FromTicks(1);
                var schedule = RyuseiSchedule.NextFire(interval, now, input.Zone, limits.MinInterval, lastHandled);
                if (!schedule.IsDue)
                {
                    return null;
                }

                scheduledFor = schedule.ScheduledFor!.Value;
                missedCount = schedule.MissedCount;
                nextRunAt = schedule.NextRunAt;
                break;

            default:
                return Malformed(trigger, "The stored spec has no time-based schedule.");
        }

        var wakeable = input.Targets.Where(t => t.Status == RyuseiFireTargetStatus.Wakeable).Select(t => t.AgentId).ToArray();
        var skipped = input.Targets.Where(t => t.Status != RyuseiFireTargetStatus.Wakeable).ToArray();
        var allGone = input.Targets.All(t => t.Status is RyuseiFireTargetStatus.Retired or RyuseiFireTargetStatus.Missing);

        // The fire moves the schedule on whatever happens to it. A one-shot timer is used up once it has fired or been skipped.
        var onwardState = isTimer ? RyuseiTriggerState.Fired : RyuseiTriggerState.Active;
        var onwardFireAt = isTimer ? null : nextRunAt;

        if (wakeable.Length == 0)
        {
            return new RyuseiFireDecision(
                RyuseiFireOutcome.SkippedResting,
                scheduledFor,
                allGone ? RyuseiTriggerState.Expired : onwardState,
                allGone ? null : onwardFireAt,
                trigger.ConsecutiveRefusals,
                [],
                null,
                SkippedCause(skipped, "no target can be woken"));
        }

        // Cooldown first, then the daily cap. The first refusal found decides the outcome.
        var cooldown = RyuseiGuards.CheckCooldown(now, input.LastWokeAt, limits.CooldownSeconds);
        var guard = cooldown.IsAllowed
            ? RyuseiGuards.CheckDailyFireCap(input.FiresInLast24Hours, limits.MaxFiresPerDay)
            : cooldown;
        if (!guard.IsAllowed)
        {
            var outcome = guard.Refusal == RyuseiRefusal.CooldownActive ? RyuseiFireOutcome.RefusedCooldown : RyuseiFireOutcome.RefusedCap;
            var consecutive = trigger.ConsecutiveRefusals + 1;
            var streak = RyuseiGuards.CheckRefusalStreak(consecutive, limits.AutoPauseAfter);

            // Hitting the daily cap pauses at once (doc 38 guard table). Any refusal pauses at the auto-pause threshold.
            var paused = outcome == RyuseiFireOutcome.RefusedCap || !streak.IsAllowed;
            return new RyuseiFireDecision(
                outcome,
                scheduledFor,
                paused ? RyuseiTriggerState.Paused : onwardState,
                onwardFireAt,
                consecutive,
                [],
                null,
                RefusedCause(guard.Refusal, consecutive, paused, missedCount));
        }

        return new RyuseiFireDecision(
            RyuseiFireOutcome.Woke,
            scheduledFor,
            onwardState,
            onwardFireAt,
            0,
            wakeable,
            WakeReason(trigger.Kind),
            WokeCause(missedCount, skipped, WakeReason(trigger.Kind)));
    }

    /// <summary>
    /// The decision for a trigger that cannot be fired as stored. The trigger pauses, so the owner sees it, and keeps its
    /// schedule so a resume after a repair can run. Its scheduled time is the stored next fire.
    /// </summary>
    public static RyuseiFireDecision Malformed(RyuseiTriggerRecord trigger, string message)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (trigger.NextFireAt is not { } scheduledFor)
        {
            throw new ArgumentException("A malformed trigger needs a stored next fire time.", nameof(trigger));
        }

        return new RyuseiFireDecision(
            RyuseiFireOutcome.Error,
            scheduledFor,
            RyuseiTriggerState.Paused,
            trigger.NextFireAt,
            trigger.ConsecutiveRefusals,
            [],
            null,
            new JsonObject { ["message"] = message });
    }

    private static JsonObject WokeCause(long missedCount, IReadOnlyList<RyuseiFireTarget> skipped, string wakeReason) => new()
    {
        ["wakeReason"] = wakeReason,
        ["missedCount"] = missedCount,
        ["skippedAgents"] = SkippedAgents(skipped),
    };

    private static JsonObject SkippedCause(IReadOnlyList<RyuseiFireTarget> skipped, string reason) => new()
    {
        ["reason"] = reason,
        ["skippedAgents"] = SkippedAgents(skipped),
    };

    private static JsonObject RefusedCause(RyuseiRefusal refusal, int consecutive, bool paused, long missedCount) => new()
    {
        ["refusal"] = refusal.ToString(),
        ["consecutiveRefusals"] = consecutive,
        ["autoPaused"] = paused,
        ["missedCount"] = missedCount,
    };

    private static JsonArray SkippedAgents(IReadOnlyList<RyuseiFireTarget> skipped)
    {
        var array = new JsonArray();
        foreach (var target in skipped)
        {
            array.Add(new JsonObject
            {
                ["agentId"] = target.AgentId.ToString("D"),
                ["status"] = target.Status.ToString().ToLowerInvariant(),
            });
        }

        return array;
    }
}
