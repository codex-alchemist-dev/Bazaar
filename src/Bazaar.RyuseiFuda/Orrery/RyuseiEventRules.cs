using System.Text.Json.Nodes;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>What one watcher match does to its trigger row. <see cref="Changed"/> is false when the match was coalesced.</summary>
public sealed record RyuseiWatcherMatch(RyuseiTriggerState NextState, DateTimeOffset? NextFireAt, bool Changed);

/// <summary>
/// What one event match does to an <c>event</c> trigger: its next state and, when the trigger wakes or skips its targets, the
/// fire row to write. <see cref="Fire"/> is null when the trigger expired by time without firing.
/// </summary>
public sealed record RyuseiEventDecision(RyuseiTriggerState NextState, RyuseiFireDecision? Fire);

/// <summary>Debounce arithmetic for watchers (doc 38, <c>debounceSeconds</c>). Pure.</summary>
public static class RyuseiDebounce
{
    /// <summary>
    /// The fire time a match arms. A match that finds a pending deadline keeps it, so a burst of matches yields one fire.
    /// Otherwise the deadline is <paramref name="now"/> plus the debounce. Because a fire is always armed by a match at or
    /// after the previous fire, consecutive fires are at least <paramref name="debounce"/> apart.
    /// </summary>
    public static DateTimeOffset Deadline(DateTimeOffset now, DateTimeOffset? pending, TimeSpan debounce)
    {
        if (debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce), debounce, "A debounce cannot be negative.");
        }

        return pending ?? now + debounce;
    }
}

/// <summary>
/// The event bridge's decisions as pure functions (doc 38, WP-R6). Nothing here reads a clock or a database; the bridge passes
/// the time and the locked row, and writes what comes back.
/// </summary>
public static class RyuseiEventRules
{
    /// <summary>
    /// Whether a trigger's predicate may see an event. Task and PR events carry a project, so the bridge already scoped the
    /// candidates to it. GC-sourced events carry none, so a predicate must name its GC; otherwise it could match a GC in
    /// another project.
    /// </summary>
    public static bool IsInScope(RyuseiPredicate predicate, RyuseiEventEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ProjectId is not null || predicate.GcId is not null;
    }

    /// <summary>
    /// A watcher's predicate matched. The watcher does not fire here: the match arms it, and the tick fires it when the debounce
    /// deadline passes (doc 38, WP-R6). An expired watcher stops instead.
    /// </summary>
    public static RyuseiWatcherMatch MatchWatcher(RyuseiTriggerRecord trigger, RyuseiWatcherTriggerSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(spec);

        if (trigger.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return new RyuseiWatcherMatch(RyuseiTriggerState.Expired, null, true);
        }

        var deadline = RyuseiDebounce.Deadline(now, trigger.NextFireAt, TimeSpan.FromSeconds(spec.DebounceSeconds));
        return new RyuseiWatcherMatch(RyuseiTriggerState.Active, deadline, trigger.NextFireAt is null);
    }

    /// <summary>
    /// An event trigger's predicate matched. It fires once: the first match wakes the wakeable targets and moves the trigger to
    /// <c>fired</c>. If no target can be woken, the fire is recorded as skipped, and the trigger is <c>expired</c> when every target
    /// is retired or missing. A trigger past its <c>expiresAt</c> expires without firing, so it writes no fire row.
    /// </summary>
    public static RyuseiEventDecision DecideEventMatch(
        RyuseiTriggerRecord trigger,
        IReadOnlyList<RyuseiFireTarget> targets,
        DateTimeOffset now,
        string eventKind)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrEmpty(eventKind);

        if (trigger.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return new RyuseiEventDecision(RyuseiTriggerState.Expired, null);
        }

        var wakeable = targets.Where(t => t.Status == RyuseiFireTargetStatus.Wakeable).Select(t => t.AgentId).ToArray();
        var skipped = targets.Count(t => t.Status != RyuseiFireTargetStatus.Wakeable);

        if (wakeable.Length == 0)
        {
            var allGone = targets.All(t => t.Status is RyuseiFireTargetStatus.Retired or RyuseiFireTargetStatus.Missing);
            var next = allGone ? RyuseiTriggerState.Expired : RyuseiTriggerState.Fired;
            var skippedFire = new RyuseiFireDecision(
                RyuseiFireOutcome.SkippedResting,
                now,
                next,
                null,
                trigger.ConsecutiveRefusals,
                [],
                null,
                new JsonObject { ["eventKind"] = eventKind, ["reason"] = "no target can be woken", ["skippedCount"] = skipped });
            return new RyuseiEventDecision(next, skippedFire);
        }

        var wakeReason = RyuseiTickRules.WakeReason(RyuseiTriggerKind.Event);
        var woke = new RyuseiFireDecision(
            RyuseiFireOutcome.Woke,
            now,
            RyuseiTriggerState.Fired,
            null,
            0,
            wakeable,
            wakeReason,
            new JsonObject { ["eventKind"] = eventKind, ["wakeReason"] = wakeReason, ["skippedCount"] = skipped });
        return new RyuseiEventDecision(RyuseiTriggerState.Fired, woke);
    }
}
