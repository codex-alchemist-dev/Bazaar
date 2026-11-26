using System.Collections.Immutable;
using Bazaar.Server.Presence.Sources;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.WorkLife.Scheduler;

/// <summary>
/// The shift scheduler (docs/design/09, "Schedule calculator" and "Availability machine"). Each <see cref="TickAsync"/> evaluates every
/// agent with <see cref="ScheduleEvaluator"/> and walks that agent's <see cref="AvailabilityMachine"/> toward the evaluated state
/// through <see cref="PresenceNotifyingAvailabilityMachine"/>, so presence sees each applied transition. It submits only the
/// events the machine defines; it creates no THROTTLED or COVERED overlay (those come from quota signals and the cover resolver).
/// </summary>
/// <remarks>
/// Walks, per doc 09: ON_SHIFT to DRAINING on shift end, a break, or a vacation; DRAINING to OFF, BREAK or VACATION once the drain gate
/// says the checklist is done; OFF or BREAK to ON_SHIFT at shift start or break end; VACATION to OFF when the calendar vacation ends
/// (never while a provider-cap vacation has not reset), then to ON_SHIFT if a shift is open. An agent already OFF or on BREAK goes straight
/// to VACATION. A first-seen agent starts OFF unless the store has a state. An owner emergency (doc 09 WP10, not built) that put the agent
/// on shift is honoured until the next schedule boundary instead of being undone on the next tick.
/// </remarks>
public sealed class ShiftScheduler(
    IAgentScheduleSource source,
    IAvailabilityStateStore store,
    IDrainGate drain,
    ShiftSchedulerOptions options,
    TimeProvider time,
    IAvailabilityTransitionSink? sink = null,
    ILogger<ShiftScheduler>? logger = null)
{
    private const int _maxStepsPerAgent = 8;

    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    private readonly Dictionary<Guid, AgentRuntime> _agents = [];
    private readonly EvaluationOptions _evaluation = new()
    {
        ExtraAvailabilityKinds = options.CalendarAvailabilityKinds.ToImmutableHashSet(StringComparer.Ordinal),
    };

    /// <summary>The machine snapshot the scheduler holds for the agent, or null when it has not seen the agent.</summary>
    public AvailabilitySnapshot? SnapshotOf(Guid agentId) => _agents.TryGetValue(agentId, out var rt) ? rt.Wrapper.Machine.Snapshot : null;

    /// <summary>
    /// Evaluates every agent once. A failure for one agent is logged and does not stop the others; a failure to list agents throws
    /// to the caller (the hosted loop catches it and ticks again).
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var entries = await source.ListAsync(ct).ConfigureAwait(false);
        var seen = entries.Select(e => e.AgentId).ToHashSet();
        foreach (var gone in _agents.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _agents.Remove(gone);
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await TickAgentAsync(entry, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                SchedulerLog.AgentFailed(_log, ex, entry.AgentId);
            }
        }
    }

    private async Task TickAgentAsync(AgentShiftEntry entry, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rt = await GetRuntimeAsync(entry.AgentId, now, ct).ConfigureAwait(false);
        var window = ScheduleEvaluator.Evaluate(entry.Schedule, entry.Events, now, _evaluation);
        var machine = rt.Wrapper.Machine;

        if (rt.EmergencyUntil is { } until && (now >= until || window.State == AvailabilityState.OnShift))
        {
            rt.EmergencyUntil = null;
        }

        if (machine.Snapshot is { State: AvailabilityMachineState.OnShift, Reason: "owner emergency" } snap
            && snap.Version != rt.EmergencyHandledVersion
            && window.State != AvailabilityState.OnShift)
        {
            rt.EmergencyHandledVersion = snap.Version;
            rt.EmergencyUntil = window.NextChangeAt ?? now + options.TickInterval;
        }

        for (var step = 0; step < _maxStepsPerAgent; step++)
        {
            var next = await NextEventAsync(entry, rt, window, now, ct).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            var transition = await rt.Wrapper.SubmitAsync(next, ct).ConfigureAwait(false);
            if (!transition.Applied)
            {
                SchedulerLog.NotApplied(_log, next.EventName, entry.AgentId, transition.Reason);
                break;
            }

            AfterApplied(rt, next, entry.Breaks, now);
        }

        var state = machine.Snapshot.State;
        if (state != rt.Saved)
        {
            await store.SaveAsync(entry.AgentId, state, ct).ConfigureAwait(false);
            rt.Saved = state;
        }
    }

    /// <summary>The one event that moves the agent a step toward the evaluated state, or null when it is where it should be.</summary>
    private async ValueTask<AvailabilityEvent?> NextEventAsync(
        AgentShiftEntry entry, AgentRuntime rt, AvailabilityWindow window, DateTimeOffset now, CancellationToken ct)
    {
        var state = rt.Wrapper.Machine.Snapshot.State;
        var target = window.State;
        switch (state)
        {
            case AvailabilityMachineState.OnShift:
                if (target == AvailabilityState.Vacation)
                {
                    return rt.EmergencyUntil is null ? new AvailabilityEvent.VacationStarts(now) : null;
                }

                if (target == AvailabilityState.Off)
                {
                    return rt.EmergencyUntil is null ? new AvailabilityEvent.ShiftEnd(now) : null;
                }

                return BreakIsDue(entry.Breaks, rt, window, now) ? new AvailabilityEvent.BreakDue(now) : null;

            case AvailabilityMachineState.Draining:
                if (!await drain.IsDrainCompleteAsync(entry.AgentId, ct).ConfigureAwait(false))
                {
                    return null;
                }

                var destination = target switch
                {
                    AvailabilityState.Vacation => AvailabilityMachineState.Vacation,
                    AvailabilityState.OnShift when rt.DrainingForBreak => AvailabilityMachineState.Break,
                    _ => AvailabilityMachineState.Off,
                };
                return new AvailabilityEvent.DrainComplete(now, destination);

            case AvailabilityMachineState.Break:
                if (target == AvailabilityState.Vacation)
                {
                    return new AvailabilityEvent.VacationStarts(now);
                }

                // A break never outlasts its length, and a shift that ended during a break is left through ON_SHIFT (the doc's table has
                // no BREAK to OFF edge).
                return target != AvailabilityState.OnShift || rt.BreakEndsAt is not { } ends || now >= ends
                    ? new AvailabilityEvent.BreakEnd(now)
                    : null;

            case AvailabilityMachineState.Off:
                return target switch
                {
                    AvailabilityState.OnShift => new AvailabilityEvent.ScheduleStart(now),
                    AvailabilityState.Vacation => new AvailabilityEvent.VacationStarts(now),
                    _ => null,
                };

            case AvailabilityMachineState.Vacation:
                return target == AvailabilityState.Vacation ? null : new AvailabilityEvent.VacationEnds(now);

            default:
                return null;
        }
    }

    /// <summary>A break is due after the policy's window of unbroken shift, and only when it ends before the shift does.</summary>
    private static bool BreakIsDue(BreakPolicy? policy, AgentRuntime rt, AvailabilityWindow window, DateTimeOffset now) =>
        policy is not null
        && now - rt.OnShiftSince >= policy.Window
        && (window.NextChangeAt is not { } boundary || now + policy.Length < boundary);

    private static void AfterApplied(AgentRuntime rt, AvailabilityEvent evt, BreakPolicy? breaks, DateTimeOffset now)
    {
        switch (evt)
        {
            case AvailabilityEvent.BreakDue:
                rt.DrainingForBreak = true;
                break;
            case AvailabilityEvent.DrainComplete { Target: AvailabilityMachineState.Break }:
                rt.DrainingForBreak = false;
                rt.BreakEndsAt = now + (breaks?.Length ?? TimeSpan.Zero);
                break;
            case AvailabilityEvent.DrainComplete:
                rt.DrainingForBreak = false;
                break;
            case AvailabilityEvent.ScheduleStart or AvailabilityEvent.BreakEnd:
                rt.OnShiftSince = now;
                rt.BreakEndsAt = null;
                break;
        }
    }

    private async ValueTask<AgentRuntime> GetRuntimeAsync(Guid agentId, DateTimeOffset now, CancellationToken ct)
    {
        if (_agents.TryGetValue(agentId, out var existing))
        {
            return existing;
        }

        var stored = await store.LoadAsync(agentId, ct).ConfigureAwait(false);
        var initial = stored ?? AvailabilityMachineState.Off;
        var machine = new AvailabilityMachine(agentId.ToString("D"), initial);
        var rt = new AgentRuntime(
            new PresenceNotifyingAvailabilityMachine(
                machine,
                agentId,
                sink,
                time,
                ex => SchedulerLog.SinkFailed(_log, ex, agentId)))
        {
            OnShiftSince = now,
            Saved = stored,
        };
        _agents[agentId] = rt;
        return rt;
    }

    private sealed class AgentRuntime(PresenceNotifyingAvailabilityMachine wrapper)
    {
        public PresenceNotifyingAvailabilityMachine Wrapper { get; } = wrapper;

        public DateTimeOffset OnShiftSince { get; set; }

        public bool DrainingForBreak { get; set; }

        public DateTimeOffset? BreakEndsAt { get; set; }

        public DateTimeOffset? EmergencyUntil { get; set; }

        public long EmergencyHandledVersion { get; set; } = -1;

        public AvailabilityMachineState? Saved { get; set; }
    }
}
