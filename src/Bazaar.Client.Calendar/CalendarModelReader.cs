using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Calendar;

/// <summary>Reads the calendar pane model into a <see cref="CalendarSnapshot"/>, raising <see cref="PaneModelException"/> on bad data.</summary>
public static class CalendarModelReader
{
    public static CalendarSnapshot Read(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("calendar model must be an object");
        }

        var mode = PaneValues.RequiredString(model, "displayTimezone") switch
        {
            "viewer" => CalendarDisplayTimezone.Viewer,
            "agent" => CalendarDisplayTimezone.Agent,
            _ => throw new PaneModelException("model field 'displayTimezone' must be 'viewer' or 'agent'"),
        };

        var events = PaneValues.Array(model, "events").EnumerateArray().Select(ReadEvent).ToArray();
        return new CalendarSnapshot(mode, events);
    }

    private static CalendarEventSummary ReadEvent(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each calendar event must be an object");
        }

        var kind = PaneValues.RequiredString(item, "kind") switch
        {
            "vacation" => CalendarEventKind.Vacation,
            "planning" => CalendarEventKind.Planning,
            "release" => CalendarEventKind.Release,
            "freeze" => CalendarEventKind.Freeze,
            "milestone" => CalendarEventKind.Milestone,
            _ => throw new PaneModelException("unknown calendar event kind"),
        };
        var status = PaneValues.RequiredString(item, "status") switch
        {
            "proposed" => CalendarEventStatus.Proposed,
            "confirmed" => CalendarEventStatus.Confirmed,
            "cancelled" => CalendarEventStatus.Cancelled,
            _ => throw new PaneModelException("unknown calendar event status"),
        };

        var startsAt = Timestamp(item, "startsAt");
        var endsAt = Timestamp(item, "endsAt");
        if (endsAt < startsAt)
        {
            throw new PaneModelException("calendar event ends before it starts");
        }

        return new CalendarEventSummary(
            PaneValues.RequiredString(item, "id"),
            kind,
            PaneValues.RequiredString(item, "title"),
            startsAt,
            endsAt,
            PaneValues.RequiredBool(item, "allDay"),
            PaneValues.RequiredString(item, "tz"),
            OptionalString(item, "agentId"),
            OptionalString(item, "projectId"),
            PaneValues.RequiredBool(item, "affectsAvailability"),
            status);
    }

    private static DateTimeOffset Timestamp(JsonElement item, string name)
    {
        var text = PaneValues.RequiredString(item, name);
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            throw new PaneModelException($"model field '{name}' must be a timestamp");
        }

        return value;
    }

    private static string? OptionalString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PaneModelException($"model field '{name}' must be a string or null");
        }

        return value.GetString();
    }
}
