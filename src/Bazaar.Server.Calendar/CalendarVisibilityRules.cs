namespace Bazaar.Server.Calendar;

/// <summary>
/// Pure per-viewer title redaction (docs/design/24, "Calendar event visibility"). The time block is never touched, so availability
/// stays honest; only the title is replaced. The company owner sees every title. An agent is its events' owner and attendee
/// (<see cref="CalendarEventRecord.AgentId"/>); an event with no agent has no attendee or owner besides the company owner.
/// </summary>
public static class CalendarVisibilityRules
{
    /// <summary>Title shown in place of one the viewer may not read.</summary>
    public const string RedactedTitle = "(private)";

    /// <summary>True when <paramref name="viewer"/> may read the title of <paramref name="record"/>.</summary>
    public static bool CanSeeTitle(CalendarEventRecord record, CalendarViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(viewer);
        if (record.Visibility == CalendarVisibility.Public || viewer.IsCompanyOwner)
            return true;

        // Attendees and owner both resolve to the event's agent for now; the empty id is the anonymous viewer and never matches.
        return viewer.Id != Guid.Empty && record.AgentId == viewer.Id;
    }

    /// <summary>The title <paramref name="viewer"/> is shown: <paramref name="title"/> when permitted, otherwise <see cref="RedactedTitle"/>.</summary>
    public static string TitleFor(CalendarEventRecord record, CalendarViewer viewer, string title) =>
        CanSeeTitle(record, viewer) ? title : RedactedTitle;
}
