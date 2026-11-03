using System.Globalization;
using Bazaar.Server.Council.Eligibility;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Tests.Council.Eligibility;

/// <summary>Shared fixtures for the Council eligibility tests: a fake schedule source and small builders.</summary>
internal static class EligibilityTestData
{
    internal static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static readonly Guid _vacationId = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");

    internal static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Weekday shift 09:00 to 17:00 UTC.</summary>
    internal static AgentSchedule WeekdayShift() =>
        new("UTC", [new ShiftWindow(new HashSet<DayOfWeek>(Weekdays), new TimeOnly(9, 0), new TimeOnly(17, 0))]);

    /// <summary>A single shift on the given days, 09:00 to 17:00 UTC.</summary>
    internal static AgentSchedule ShiftOn(params DayOfWeek[] days) =>
        new("UTC", [new ShiftWindow(new HashSet<DayOfWeek>(days), new TimeOnly(9, 0), new TimeOnly(17, 0))]);

    /// <summary>A vacation event that makes the agent unavailable over [start, end).</summary>
    internal static CalendarEvent Vacation(string start, string end) =>
        new(_vacationId, "vacation", At(start), At(end), affectsAvailability: true);

    internal static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.Ordinal);

    internal static IReadOnlyList<string> Sorted(IEnumerable<string> ids) => ids.Order(StringComparer.Ordinal).ToList();
}

/// <summary>Fake source of schedule inputs. Agents not added have no schedule on file.</summary>
internal sealed class FakeVoterSchedules : IVoterSchedules
{
    private readonly Dictionary<string, VoterScheduleInputs> _inputs = new(StringComparer.Ordinal);

    internal FakeVoterSchedules Add(string agentId, AgentSchedule schedule, params CalendarEvent[] events)
    {
        _inputs[agentId] = new VoterScheduleInputs(schedule, events);
        return this;
    }

    public VoterScheduleInputs? GetInputs(string agentId) =>
        _inputs.TryGetValue(agentId, out var inputs) ? inputs : null;
}
