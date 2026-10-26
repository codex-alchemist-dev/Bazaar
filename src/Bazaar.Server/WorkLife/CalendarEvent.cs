namespace Bazaar.Server.WorkLife;

/// <summary>
/// A Company Calendar event as the schedule calculator sees it (doc 24 owns the table; doc 09 consumes it).
/// The event covers the half-open instant range [<see cref="Start"/>, <see cref="End"/>).
/// </summary>
public sealed record CalendarEvent
{
    /// <summary>Creates an event. Throws <see cref="ArgumentException"/> when <paramref name="end"/> is not after <paramref name="start"/>.</summary>
    public CalendarEvent(Guid id, string kind, DateTimeOffset start, DateTimeOffset end, bool affectsAvailability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (end <= start)
        {
            throw new ArgumentException("An event must end after it starts.", nameof(end));
        }

        Id = id;
        Kind = kind;
        Start = start;
        End = end;
        AffectsAvailability = affectsAvailability;
    }

    /// <summary>Event identity. Used in vacation reasons as <c>vacation:&lt;id&gt;</c>.</summary>
    public Guid Id { get; }

    /// <summary>Event kind, for example <c>holiday</c> or <c>meeting</c>.</summary>
    public string Kind { get; }

    /// <summary>Inclusive start instant.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>Exclusive end instant.</summary>
    public DateTimeOffset End { get; }

    /// <summary>True when the event makes the agent unavailable (a vacation block).</summary>
    public bool AffectsAvailability { get; }
}
