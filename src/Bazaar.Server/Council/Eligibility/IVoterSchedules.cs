using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Council.Eligibility;

/// <summary>
/// What the schedule calculator needs for one voter: the schedule, the calendar events that can make the voter unavailable,
/// and any operator additions to the evaluation.
/// </summary>
/// <param name="Schedule">The voter's shifts, curfew and zone.</param>
/// <param name="Events">Calendar events for the voter. Only those that affect availability can produce a vacation.</param>
/// <param name="Options">Operator additions to the evaluation; null uses the defaults.</param>
public sealed record VoterScheduleInputs(
    AgentSchedule Schedule,
    IReadOnlyList<CalendarEvent> Events,
    EvaluationOptions? Options = null);

/// <summary>
/// Supplies each voter's schedule inputs. Implemented by the caller over the Work-Life store; tests use a fake.
/// </summary>
public interface IVoterSchedules
{
    /// <summary>Returns the voter's schedule inputs, or null when the agent has no schedule on file.</summary>
    VoterScheduleInputs? GetInputs(string agentId);
}
