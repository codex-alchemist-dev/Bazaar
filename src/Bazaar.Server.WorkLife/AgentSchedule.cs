namespace Bazaar.Server.WorkLife;

/// <summary>
/// One weekly shift: the days it starts on and its local start and end times. A shift is anchored on the day
/// it starts, so when <see cref="LocalEnd"/> is not after <see cref="LocalStart"/> it crosses midnight and
/// ends on the following day.
/// </summary>
public sealed record ShiftWindow
{
    /// <summary>Creates a shift. Throws <see cref="ArgumentException"/> for an empty day set or equal start and end.</summary>
    public ShiftWindow(IReadOnlySet<DayOfWeek> days, TimeOnly localStart, TimeOnly localEnd)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
        {
            throw new ArgumentException("A shift needs at least one day.", nameof(days));
        }

        if (localStart == localEnd)
        {
            throw new ArgumentException("A shift cannot start and end at the same local time.", nameof(localEnd));
        }

        Days = days;
        LocalStart = localStart;
        LocalEnd = localEnd;
    }

    /// <summary>Days of the week on which the shift starts, in the agent's local zone.</summary>
    public IReadOnlySet<DayOfWeek> Days { get; }

    /// <summary>Local start time.</summary>
    public TimeOnly LocalStart { get; }

    /// <summary>Local end time; not after <see cref="LocalStart"/> means the shift crosses midnight.</summary>
    public TimeOnly LocalEnd { get; }
}

/// <summary>
/// A daily local time window during which the agent is off (curfew). It may cross midnight.
/// </summary>
public sealed record CurfewWindow
{
    /// <summary>Creates a curfew. Throws <see cref="ArgumentException"/> for equal start and end.</summary>
    public CurfewWindow(TimeOnly localStart, TimeOnly localEnd)
    {
        if (localStart == localEnd)
        {
            throw new ArgumentException("A curfew cannot start and end at the same local time.", nameof(localEnd));
        }

        LocalStart = localStart;
        LocalEnd = localEnd;
    }

    /// <summary>Local start time.</summary>
    public TimeOnly LocalStart { get; }

    /// <summary>Local end time; not after <see cref="LocalStart"/> means the curfew crosses midnight.</summary>
    public TimeOnly LocalEnd { get; }
}

/// <summary>
/// An agent's simulated schedule: its IANA zone, weekly shifts and optional daily curfew. Stored in
/// <c>agent_schedule</c> (doc 09); this type is the in-memory form the calculator reads.
/// </summary>
public sealed record AgentSchedule
{
    /// <summary>
    /// Creates a schedule. <paramref name="tz"/> must be an IANA zone id known to the machine; otherwise
    /// <see cref="ArgumentException"/> is thrown.
    /// </summary>
    public AgentSchedule(string tz, IReadOnlyList<ShiftWindow> shifts, CurfewWindow? curfew = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tz);
        ArgumentNullException.ThrowIfNull(shifts);

        Tz = tz;
        Zone = ResolveZone(tz);
        Shifts = shifts.ToArray();
        Curfew = curfew;
    }

    /// <summary>IANA zone id, for example <c>Europe/London</c>.</summary>
    public string Tz { get; }

    /// <summary>The resolved zone that every local time in this schedule refers to.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>Weekly shifts. Empty means the agent has no shift.</summary>
    public IReadOnlyList<ShiftWindow> Shifts { get; }

    /// <summary>Daily curfew, or null for none.</summary>
    public CurfewWindow? Curfew { get; }

    private static TimeZoneInfo ResolveZone(string tz)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"'{tz}' is not a known IANA time zone on this machine.", nameof(tz), ex);
        }
    }
}
