using System.Text.Json.Nodes;
using Bazaar.Server.Agents.Inbox;

namespace Bazaar.RyuseiFuda.Orrery;

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

    /// <summary>
    /// A guard trip on chain depth: the trigger is deeper in a chain than the setting allows (doc 38). The trigger is paused and the
    /// trip goes to <c>guard_event</c> and the Psych Ward hook.
    /// </summary>
    RefusedLoop,

    /// <summary>Another fire of this trigger is still in flight, so this one is skipped (doc 38, single flight).</summary>
    SkippedInflight,
}

/// <summary>What the tick reads for one due trigger. The rules take it as a value; nothing is read from a clock or database here.</summary>
/// <param name="Trigger">The locked row as it is stored now.</param>
/// <param name="Spec">The decoded spec.</param>
/// <param name="Zone">The zone that anchors an interval's wall-clock days (doc 09).</param>
/// <param name="Targets">Each target agent and its status.</param>
/// <param name="FiresInLast24Hours">Woke fires of this trigger in the last 24 hours.</param>
/// <param name="LastWokeAt">The time of the latest woke fire, or null when there is none.</param>
/// <param name="WakesInLastHour">The highest count of wakes in the last hour over the trigger's targets, all reasons (doc 38, guard table).</param>
/// <param name="FireInFlight">True when another fire of this trigger is still being written (doc 38, single flight). The claim's row lock keeps this false in practice.</param>
public sealed record RyuseiTickInput(
    RyuseiTriggerRecord Trigger,
    RyuseiTriggerSpec Spec,
    TimeZoneInfo Zone,
    IReadOnlyList<RyuseiFireTarget> Targets,
    int FiresInLast24Hours,
    DateTimeOffset? LastWokeAt,
    int WakesInLastHour = 0,
    bool FireInFlight = false);

/// <summary>
/// The result for one due trigger: the fire row to insert, and how the trigger row changes. <see cref="WakeAgentIds"/> is
/// empty unless <see cref="Outcome"/> is <see cref="RyuseiFireOutcome.Woke"/>. <see cref="Trip"/> is set when a guard paused the
/// trigger: it names the refusal, the trip goes to <c>guard_event</c>, and the Psych Ward hook is called (doc 38).
/// </summary>
public sealed record RyuseiFireDecision(
    RyuseiFireOutcome Outcome,
    DateTimeOffset ScheduledFor,
    RyuseiTriggerState NextState,
    DateTimeOffset? NextFireAt,
    int ConsecutiveRefusals,
    IReadOnlyList<Guid> WakeAgentIds,
    string? WakeReason,
    JsonObject Cause,
    RyuseiRefusal? Trip = null)
{
    /// <summary>True when the fire woke its targets and so counts toward the fire count and the daily cap.</summary>
    public bool Woke => Outcome == RyuseiFireOutcome.Woke;

    /// <summary>True when a guard tripped and paused the trigger.</summary>
    public bool IsTrip => Trip is not null;
}

/// <summary>
/// Which due triggers fire at an instant, and how they change (doc 38, Layer 2 firing and Loop and runaway safeguards).
/// Pure: no clock, no database. The tick service reads the rows, calls <see cref="Decide"/> and writes what it returns.
/// </summary>
/// <remarks>
/// Decisions, in order: a trigger that is not active or not due is left alone (null). A fire that finds another fire of the
/// trigger in flight is skipped and moves nothing (single flight). A fire whose targets are all resting or retired is
/// skipped. A trigger deeper than the chain limit is a loop trip and pauses. A fire inside the cooldown, past the daily cap
/// or over the shared wake cap is refused. Otherwise the fire wakes every wakeable target. A refusal counts toward
/// auto-pause. Every fire that is decided, woke or refused, moves the schedule on, so a refused occurrence is never
/// retried at the same scheduled time.
/// </remarks>
public static class RyuseiTickRules
{
    /// <summary>
    /// The kinds the tick fires. A watcher is tickable, but only once an event has armed it: the event bridge (WP-R6) sets its
    /// <c>next_fire_at</c> to the debounce deadline, and an idle watcher has none. An event trigger is never tickable; it fires
    /// on its first match in the bridge.
    /// </summary>
    public static bool IsTickable(RyuseiTriggerKind kind) => kind is RyuseiTriggerKind.Timer or RyuseiTriggerKind.Interval or RyuseiTriggerKind.Watcher;

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
        RyuseiFireOutcome.RefusedLoop => "refused_loop",
        RyuseiFireOutcome.SkippedInflight => "skipped_inflight",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a fire outcome."),
    };

    /// <summary>
    /// The <c>guard_event.kind</c> for a guard trip (doc 06 guard_event, doc 38 guard table). Only the three refusals that pause a
    /// trigger are trips; any other refusal is not.
    /// </summary>
    public static string GuardEventKind(RyuseiRefusal trip) => trip switch
    {
        RyuseiRefusal.ChainDepthExceeded => "ryusei_chain_depth",
        RyuseiRefusal.DailyFireCapReached => "ryusei_daily_fire_cap",
        RyuseiRefusal.AutoPauseTripped => "ryusei_refusal_streak",
        _ => throw new ArgumentOutOfRangeException(nameof(trip), trip, "Not a guard trip."),
    };

    /// <summary>
    /// True for a watcher past its <c>expiresAt</c>. The tick expires it without a fire row, since nothing fired.
    /// </summary>
    public static bool IsExpiredWatcher(RyuseiTriggerRecord trigger, DateTimeOffset now) =>
        trigger.Kind == RyuseiTriggerKind.Watcher && trigger.ExpiresAt is { } expiresAt && expiresAt <= now;

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

            case RyuseiWatcherTriggerSpec:
                // The event bridge armed this watcher: next_fire_at is its debounce deadline (doc 38, WP-R6). It has no schedule to advance.
                scheduledFor = nextFireAt;
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

        // Single flight comes first: a fire that finds another fire of this trigger in flight is skipped and moves nothing, so the
        // schedule is left for the fire in flight to advance.
        var flight = RyuseiGuards.CheckSingleFlight(input.FireInFlight);
        if (!flight.IsAllowed)
        {
            return new RyuseiFireDecision(
                RyuseiFireOutcome.SkippedInflight,
                scheduledFor,
                trigger.State,
                trigger.NextFireAt,
                trigger.ConsecutiveRefusals,
                [],
                null,
                new JsonObject { ["reason"] = "another fire of this trigger is still in flight" });
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

        // Chain depth comes before the cooldown and the caps. A trigger deeper than the setting allows is a loop trip and pauses at once.
        // It is checked only when someone can be woken, so a deep trigger whose targets are all resting is skipped, not tripped.
        var chain = RyuseiGuards.CheckChainDepth(trigger.ChainDepth, limits.MaxChainDepth);
        if (!chain.IsAllowed)
        {
            var consecutive = trigger.ConsecutiveRefusals + 1;
            return new RyuseiFireDecision(
                RyuseiFireOutcome.RefusedLoop,
                scheduledFor,
                RyuseiTriggerState.Paused,
                onwardFireAt,
                consecutive,
                [],
                null,
                RefusedCause(chain.Refusal, consecutive, paused: true, missedCount),
                Trip: chain.Refusal);
        }

        // Ping fan-out first: a ping names every woken target, so a fan-out above the cap is refused before any line is posted. The
        // cap is read at fire time, so a lowered setting takes effect on the next fire. Then cooldown, the daily cap and the shared
        // wake cap (CheckWake). The first refusal found decides the outcome.
        var fanout = trigger.PingMode == RyuseiPingMode.None
            ? RyuseiDecision.Allow
            : RyuseiGuards.CheckPingFanout(wakeable.Length, limits.MaxPingFanout);
        var guard = fanout.IsAllowed
            ? RyuseiGuards.CheckWake(now, input.LastWokeAt, input.FiresInLast24Hours, input.WakesInLastHour, limits)
            : fanout;
        if (!guard.IsAllowed)
        {
            var outcome = guard.Refusal == RyuseiRefusal.CooldownActive ? RyuseiFireOutcome.RefusedCooldown : RyuseiFireOutcome.RefusedCap;
            var consecutive = trigger.ConsecutiveRefusals + 1;
            var streak = RyuseiGuards.CheckRefusalStreak(consecutive, limits.AutoPauseAfter);

            // Only the daily fire cap pauses at once (doc 38 guard table). The wake cap, the cooldown and the ping fan-out do not;
            // every refusal still counts toward the auto-pause streak. A streak that reaches the threshold is the trip.
            var dailyCap = guard.Refusal == RyuseiRefusal.DailyFireCapReached;
            var paused = dailyCap || !streak.IsAllowed;
            RyuseiRefusal? trip = dailyCap ? RyuseiRefusal.DailyFireCapReached : paused ? RyuseiRefusal.AutoPauseTripped : null;
            var cause = RefusedCause(guard.Refusal, consecutive, paused, missedCount);
            if (guard.Refusal == RyuseiRefusal.PingFanoutExceeded)
            {
                // The fire is recorded with its reason. No line is posted and nobody is woken, so the ping is skipped, not dropped.
                cause["pingSkipped"] = true;
                cause["fanout"] = wakeable.Length;
                cause["cap"] = Math.Clamp(limits.MaxPingFanout, 1, RyuseiDefaults.MaxPingFanout);
            }

            return new RyuseiFireDecision(
                outcome,
                scheduledFor,
                paused ? RyuseiTriggerState.Paused : onwardState,
                onwardFireAt,
                consecutive,
                [],
                null,
                cause,
                trip);
        }

        // A watcher that has reached its max fires is used up by the fire that woke its targets (doc 38, Trigger types).
        var reachedMaxFires = trigger.Kind == RyuseiTriggerKind.Watcher && trigger.MaxFires is { } maxFires && trigger.FireCount + 1 >= maxFires;
        return new RyuseiFireDecision(
            RyuseiFireOutcome.Woke,
            scheduledFor,
            reachedMaxFires ? RyuseiTriggerState.Fired : onwardState,
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

    /// <summary>
    /// A fire refused by a wake guard (doc 38, guard table). The outcome is a cooldown or cap refusal. Only the daily fire cap, or
    /// a streak at the auto-pause threshold, pauses the trigger; the other refusals keep <paramref name="onwardState"/>, so the
    /// trigger is not lost. Every refusal counts toward the streak.
    /// </summary>
    public static RyuseiFireDecision Refuse(
        RyuseiTriggerRecord trigger,
        RyuseiRefusal refusal,
        DateTimeOffset scheduledFor,
        RyuseiTriggerState onwardState,
        DateTimeOffset? onwardFireAt,
        long missedCount,
        RyuseiLimits limits,
        int fanout = 0)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(limits);

        var outcome = refusal == RyuseiRefusal.CooldownActive ? RyuseiFireOutcome.RefusedCooldown : RyuseiFireOutcome.RefusedCap;
        var consecutive = trigger.ConsecutiveRefusals + 1;
        var streak = RyuseiGuards.CheckRefusalStreak(consecutive, limits.AutoPauseAfter);
        var paused = refusal == RyuseiRefusal.DailyFireCapReached || !streak.IsAllowed;
        var cause = RefusedCause(refusal, consecutive, paused, missedCount);
        if (refusal == RyuseiRefusal.PingFanoutExceeded)
        {
            // The fire is recorded with its reason. No line is posted and nobody is woken, so the ping is skipped, not dropped.
            cause["pingSkipped"] = true;
            cause["fanout"] = fanout;
            cause["cap"] = Math.Clamp(limits.MaxPingFanout, 1, RyuseiDefaults.MaxPingFanout);
        }

        return new RyuseiFireDecision(
            outcome,
            scheduledFor,
            paused ? RyuseiTriggerState.Paused : onwardState,
            onwardFireAt,
            consecutive,
            [],
            null,
            cause);
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
