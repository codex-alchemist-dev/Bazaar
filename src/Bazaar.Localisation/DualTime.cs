using System.Globalization;

namespace Bazaar.Localisation;

/// <summary>The viewer-local and agent-local renderings of one instant.</summary>
/// <param name="Viewer">The instant in the viewer's timezone, in the viewer's locale.</param>
/// <param name="Agent">The instant in the agent's timezone, in the viewer's locale.</param>
/// <param name="SameClock">True when both zones show the same wall-clock time, so the head can show one.</param>
public readonly record struct DualTime(string Viewer, string Agent, bool SameClock);

/// <summary>
/// Doc 49 section 2: dates and times use the viewer's locale, each agent keeps its own timezone, and the viewer sees both
/// the agent's local time and their own.
/// </summary>
public static class DualTimeFormatter
{
    /// <summary>Formats <paramref name="instant"/> for the viewer and for the agent's zone, both in the viewer's culture.</summary>
    public static DualTime Format(DateTimeOffset instant, TimeZoneInfo viewerZone, TimeZoneInfo agentZone, CultureInfo viewerCulture)
    {
        ArgumentNullException.ThrowIfNull(viewerZone);
        ArgumentNullException.ThrowIfNull(agentZone);
        ArgumentNullException.ThrowIfNull(viewerCulture);

        DateTimeOffset viewer = TimeZoneInfo.ConvertTime(instant, viewerZone);
        DateTimeOffset agent = TimeZoneInfo.ConvertTime(instant, agentZone);
        return new DualTime(
            viewer.ToString("g", viewerCulture),
            agent.ToString("g", viewerCulture),
            viewer.DateTime == agent.DateTime);
    }
}
