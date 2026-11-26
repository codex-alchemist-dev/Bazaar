namespace Bazaar.Server.WorkLife.Scheduler.Abstractions;

// Ports the shift scheduler reads and writes (docs/design/09, "Schedule calculator", "Availability machine" and "Drain and handover").
// The repositories for agent_schedule and availability (doc 09 WP3) and the drain checklist (WP5) implement these later.

/// <summary>
/// A time-based break policy, read from the <c>breaks</c> object of the schedule model (doc 09): after <see cref="Window"/> of
/// unbroken shift the agent takes a break of <see cref="Length"/>. The doc's field names are <c>windowHours</c> and
/// <c>lengthMin</c>; reading them as "break every window, for length" is an interpretation of a PROPOSED item.
/// </summary>
public sealed record BreakPolicy
{
    /// <summary>Creates a policy. Throws <see cref="ArgumentOutOfRangeException"/> for a non-positive window or length.</summary>
    public BreakPolicy(TimeSpan window, TimeSpan length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero);
        Window = window;
        Length = length;
    }

    /// <summary>Continuous time on shift before a break is due.</summary>
    public TimeSpan Window { get; }

    /// <summary>How long the break lasts.</summary>
    public TimeSpan Length { get; }
}

/// <summary>One agent as the scheduler sees it: its schedule, the calendar events that affect it and an optional break policy.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Schedule">Shifts, curfew and IANA zone.</param>
/// <param name="Events">Calendar events for this agent (doc 24). Only those that affect availability change anything.</param>
/// <param name="Breaks">Optional break policy; null means no scheduled breaks.</param>
public sealed record AgentShiftEntry(
    Guid AgentId,
    AgentSchedule Schedule,
    IReadOnlyList<CalendarEvent> Events,
    BreakPolicy? Breaks = null);

/// <summary>The agents to schedule, with their schedules and calendar events. Doc 09 WP3 and WP8 give the durable implementation.</summary>
public interface IAgentScheduleSource
{
    /// <summary>Returns every agent that has a schedule right now.</summary>
    ValueTask<IReadOnlyList<AgentShiftEntry>> ListAsync(CancellationToken ct);
}

/// <summary>The last known availability state per agent, so a restart resumes from it (doc 09, "Behaviour and edge cases").</summary>
public interface IAvailabilityStateStore
{
    /// <summary>Returns the stored state, or null when the agent has none.</summary>
    ValueTask<AvailabilityMachineState?> LoadAsync(Guid agentId, CancellationToken ct);

    /// <summary>Stores the state after a change.</summary>
    ValueTask SaveAsync(Guid agentId, AvailabilityMachineState state, CancellationToken ct);
}

/// <summary>
/// The drain checklist gate (doc 09, "Drain"). While an agent is DRAINING the scheduler asks it each tick; DRAINING ends when it
/// answers true. The checklist itself (WP5) is not built, so the default answers true at once.
/// </summary>
public interface IDrainGate
{
    /// <summary>Returns true when the agent's drain checklist is complete.</summary>
    ValueTask<bool> IsDrainCompleteAsync(Guid agentId, CancellationToken ct);
}
