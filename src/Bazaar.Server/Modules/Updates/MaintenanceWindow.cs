namespace Bazaar.Server.Modules.Updates;

/// <summary>
/// The clock a window is read in: a named time zone (with its daylight saving rules) or a fixed UTC offset. Exactly one of
/// the two is set; <see cref="IsValid"/> is false otherwise.
/// </summary>
public sealed record MaintenanceZone
{
    /// <summary>The largest offset a fixed zone may carry. Real offsets run from -12:00 to +14:00.</summary>
    public static TimeSpan MaxFixedOffset { get; } = TimeSpan.FromHours(14);

    private MaintenanceZone(TimeZoneInfo? timeZone, TimeSpan? fixedOffset)
    {
        NamedZone = timeZone;
        FixedOffset = fixedOffset;
    }

    /// <summary>A named zone. A null zone is kept so that validation can report it.</summary>
    public TimeZoneInfo? NamedZone { get; }

    /// <summary>A fixed UTC offset, or null for a named zone.</summary>
    public TimeSpan? FixedOffset { get; }

    /// <summary>True when exactly one clock is set and a fixed offset is within the allowed range.</summary>
    public bool IsValid =>
        (NamedZone is null) != (FixedOffset is null)
        && (FixedOffset is not { } offset || (offset >= -MaxFixedOffset && offset <= MaxFixedOffset));

    public static MaintenanceZone Named(TimeZoneInfo? timeZone) => new(timeZone, null);

    public static MaintenanceZone Fixed(TimeSpan offset) => new(null, offset);

    /// <summary>The UTC offset in force at an instant.</summary>
    public TimeSpan OffsetAt(DateTimeOffset instant) =>
        NamedZone is { } zone ? zone.GetUtcOffset(instant) : FixedOffset ?? TimeSpan.Zero;

    /// <summary>The instant of a local wall-clock time in this zone. A time in a daylight saving gap takes the offset the zone reports.</summary>
    public DateTimeOffset ToInstant(DateTime localWallClock) =>
        new(localWallClock, NamedZone is { } zone ? zone.GetUtcOffset(localWallClock) : FixedOffset ?? TimeSpan.Zero);
}

/// <summary>
/// A weekly maintenance window: on each listed day, from <see cref="Start"/> (inclusive) to <see cref="End"/> (exclusive), in
/// <see cref="Zone"/>. A window does not cross midnight; an overnight window is written as two windows.
/// </summary>
public sealed record MaintenanceWindow(IReadOnlyList<DayOfWeek> Days, TimeOnly Start, TimeOnly End, MaintenanceZone Zone)
{
    /// <summary>True when the instant falls inside this window's weekly slot.</summary>
    public bool Contains(DateTimeOffset instant)
    {
        var local = instant.ToOffset(Zone.OffsetAt(instant));
        var time = TimeOnly.FromDateTime(local.DateTime);
        return Days.Contains(local.DayOfWeek) && time >= Start && time < End;
    }

    /// <summary>The first start of this window strictly after the instant, or null when the window has no valid days.</summary>
    public DateTimeOffset? NextStartAfter(DateTimeOffset instant)
    {
        var localDate = DateOnly.FromDateTime(instant.ToOffset(Zone.OffsetAt(instant)).DateTime);
        DateTimeOffset? next = null;

        // Eight days covers every weekday, including today's slot when it has already started.
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = localDate.AddDays(offset);
            if (!Days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var start = Zone.ToInstant(date.ToDateTime(Start));
            if (start > instant && (next is null || start < next))
            {
                next = start;
            }
        }

        return next;
    }
}

/// <summary>The kinds of problem a window or a window set can have.</summary>
public enum MaintenanceWindowIssue
{
    /// <summary>The window lists no days.</summary>
    NoDays,

    /// <summary>Start equals End, so the window is empty.</summary>
    EmptyRange,

    /// <summary>Start is after End. Windows do not cross midnight.</summary>
    InvertedRange,

    /// <summary>The zone is missing, or the fixed offset is outside the allowed range.</summary>
    InvalidZone,

    /// <summary>The window shares an instant with another window of the same set.</summary>
    Overlap,
}

/// <summary>One problem, with the index of the window it concerns and, for an overlap, the index of the other window.</summary>
public sealed record MaintenanceWindowProblem(int WindowIndex, MaintenanceWindowIssue Issue, int? OtherWindowIndex = null);

/// <summary>The result of validating a window set. Valid when <see cref="Problems"/> is empty.</summary>
public sealed record MaintenanceWindowValidation(IReadOnlyList<MaintenanceWindowProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Validates maintenance windows without throwing. Overlap is checked on one reference week (Monday 2026-01-05 to Sunday
/// 2026-01-11, UTC-based offsets for that week), so two windows that only meet in a daylight saving change week are not
/// caught.
/// </summary>
public static class MaintenanceWindowValidator
{
    private static readonly DateOnly _referenceMonday = new(2026, 1, 5);

    /// <summary>Checks each window, then every pair for overlap. Returns all problems found, not just the first.</summary>
    public static MaintenanceWindowValidation Validate(IReadOnlyList<MaintenanceWindow> windows)
    {
        var problems = new List<MaintenanceWindowProblem>();

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (window.Days.Count == 0)
            {
                problems.Add(new MaintenanceWindowProblem(i, MaintenanceWindowIssue.NoDays));
            }

            if (window.Start == window.End)
            {
                problems.Add(new MaintenanceWindowProblem(i, MaintenanceWindowIssue.EmptyRange));
            }
            else if (window.Start > window.End)
            {
                problems.Add(new MaintenanceWindowProblem(i, MaintenanceWindowIssue.InvertedRange));
            }

            if (!window.Zone.IsValid)
            {
                problems.Add(new MaintenanceWindowProblem(i, MaintenanceWindowIssue.InvalidZone));
            }
        }

        // Overlap needs a real slot, so only windows that have days, a non-empty forward range and a valid zone take part.
        var slots = new List<(DateTimeOffset Start, DateTimeOffset End)>[windows.Count];
        for (var i = 0; i < windows.Count; i++)
        {
            slots[i] = ReferenceSlots(windows[i]);
        }

        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                if (SlotsOverlap(slots[i], slots[j]))
                {
                    problems.Add(new MaintenanceWindowProblem(j, MaintenanceWindowIssue.Overlap, i));
                }
            }
        }

        return new MaintenanceWindowValidation(problems);
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> ReferenceSlots(MaintenanceWindow window)
    {
        var slots = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        if (window.Days.Count == 0 || window.Start >= window.End || !window.Zone.IsValid)
        {
            return slots;
        }

        foreach (var day in window.Days.Distinct())
        {
            // Monday is the first day of the reference week; Sunday (0) is the last.
            var date = _referenceMonday.AddDays(((int)day + 6) % 7);
            slots.Add((window.Zone.ToInstant(date.ToDateTime(window.Start)), window.Zone.ToInstant(date.ToDateTime(window.End))));
        }

        return slots;
    }

    private static bool SlotsOverlap(
        List<(DateTimeOffset Start, DateTimeOffset End)> left,
        List<(DateTimeOffset Start, DateTimeOffset End)> right)
    {
        foreach (var a in left)
        {
            foreach (var b in right)
            {
                if (a.Start < b.End && b.Start < a.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The earliest start after the instant across a set of windows, or null when none has a start.</summary>
    public static DateTimeOffset? NextStartAfter(IReadOnlyList<MaintenanceWindow> windows, DateTimeOffset instant)
    {
        DateTimeOffset? next = null;
        foreach (var window in windows)
        {
            var start = window.NextStartAfter(instant);
            if (start is { } candidate && (next is null || candidate < next))
            {
                next = candidate;
            }
        }

        return next;
    }

    /// <summary>True when the instant falls inside any window of the set.</summary>
    public static bool ContainsAny(IReadOnlyList<MaintenanceWindow> windows, DateTimeOffset instant) =>
        windows.Any(window => window.Contains(instant));
}
