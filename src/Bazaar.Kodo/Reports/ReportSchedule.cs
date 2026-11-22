namespace Bazaar.Kodo.Reports;

/// <summary>How often a scheduled report is due (doc 46 section 16.7). Monthly is not built.</summary>
public enum ReportCadence
{
    Daily,
    Weekly,
}

/// <summary>
/// One UTC offset that applies from an instant onward. A list of these, in order, is the time zone rule set: there is no zone
/// database in this core, so the operator or the host supplies the offsets (for example the two a year of a daylight saving zone).
/// </summary>
/// <param name="EffectiveFrom">The UTC instant the offset starts to apply.</param>
/// <param name="Offset">The offset from UTC, in whole minutes, from -14 to +14 hours.</param>
public sealed record ReportOffsetRule(DateTimeOffset EffectiveFrom, TimeSpan Offset);

/// <summary>
/// When a report is due: a local time of day (daily) or a day of the week and a local time (weekly), in a zone given as explicit
/// offset rules. Pure and deterministic: the same schedule and the same instant always give the same answer, and no clock is read.
/// </summary>
/// <remarks>
/// A local time skipped by a clock change (the spring gap) is due at the moment the clock jumps. A local time that happens twice (the
/// autumn overlap) is due at its first occurrence.
/// </remarks>
public sealed class ReportSchedule
{
    /// <summary>The most offset rules one schedule may carry.</summary>
    public const int MaxRules = 64;

    private static readonly DateTimeOffset _earliest = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _latest = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ReportOffsetRule[] _rules;

    /// <summary>Creates a schedule. Throws <see cref="ArgumentException"/> for a rule set or time that is not valid.</summary>
    /// <param name="cadence">Daily or weekly.</param>
    /// <param name="timeOfDay">The local time, from 00:00 up to but not including 24:00, in whole minutes.</param>
    /// <param name="rules">The offset rules, strictly increasing by effective instant. The first one also covers every earlier instant.</param>
    /// <param name="dayOfWeek">The local day for a weekly report. Ignored for a daily one.</param>
    public ReportSchedule(ReportCadence cadence, TimeSpan timeOfDay, IReadOnlyList<ReportOffsetRule> rules, DayOfWeek dayOfWeek = DayOfWeek.Monday)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Enum.IsDefined(cadence))
        {
            throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Not a report cadence.");
        }

        if (!Enum.IsDefined(dayOfWeek))
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, "Not a day of the week.");
        }

        if (timeOfDay < TimeSpan.Zero || timeOfDay >= TimeSpan.FromDays(1) || timeOfDay.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("The time of day must be from 00:00 to 23:59, in whole minutes.", nameof(timeOfDay));
        }

        if (rules.Count is 0 or > MaxRules)
        {
            throw new ArgumentException($"A schedule needs from 1 to {MaxRules} offset rules.", nameof(rules));
        }

        for (int index = 0; index < rules.Count; index++)
        {
            ReportOffsetRule? rule = rules[index];
            if (rule is null)
            {
                throw new ArgumentException("An offset rule must not be null.", nameof(rules));
            }

            if (rule.Offset < TimeSpan.FromHours(-14) || rule.Offset > TimeSpan.FromHours(14) || rule.Offset.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                throw new ArgumentException("An offset must be from -14 to +14 hours, in whole minutes.", nameof(rules));
            }

            if (rule.EffectiveFrom.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("An effective instant must be given in UTC.", nameof(rules));
            }

            if (index > 0 && rule.EffectiveFrom <= rules[index - 1].EffectiveFrom)
            {
                throw new ArgumentException("Offset rules must be strictly increasing by effective instant.", nameof(rules));
            }
        }

        Cadence = cadence;
        TimeOfDay = timeOfDay;
        DayOfWeek = dayOfWeek;
        _rules = [.. rules];
    }

    /// <summary>Daily or weekly.</summary>
    public ReportCadence Cadence { get; }

    /// <summary>The local time of day.</summary>
    public TimeSpan TimeOfDay { get; }

    /// <summary>The local day for a weekly report.</summary>
    public DayOfWeek DayOfWeek { get; }

    /// <summary>The offset rules, in order.</summary>
    public IReadOnlyList<ReportOffsetRule> Rules => _rules;

    /// <summary>A schedule in a zone that never changes offset.</summary>
    public static ReportSchedule Fixed(ReportCadence cadence, TimeSpan timeOfDay, TimeSpan offset, DayOfWeek dayOfWeek = DayOfWeek.Monday) =>
        new(cadence, timeOfDay, [new ReportOffsetRule(DateTimeOffset.UnixEpoch, offset)], dayOfWeek);

    /// <summary>The offset in force at an instant.</summary>
    public TimeSpan OffsetAt(DateTimeOffset instant)
    {
        TimeSpan offset = _rules[0].Offset;
        foreach (ReportOffsetRule rule in _rules)
        {
            if (rule.EffectiveFrom > instant)
            {
                break;
            }

            offset = rule.Offset;
        }

        return offset;
    }

    /// <summary>The first due instant strictly after <paramref name="after"/>, in UTC.</summary>
    public DateTimeOffset NextDue(DateTimeOffset after)
    {
        foreach (DateTimeOffset slot in Around(after))
        {
            if (slot > after)
            {
                return slot;
            }
        }

        throw new InvalidOperationException("No due instant was found; the offset rules are not usable.");
    }

    /// <summary>The last due instant strictly before <paramref name="before"/>, in UTC.</summary>
    public DateTimeOffset PreviousDue(DateTimeOffset before)
    {
        DateTimeOffset? found = null;
        foreach (DateTimeOffset slot in Around(before))
        {
            if (slot < before)
            {
                found = slot;
            }
        }

        return found ?? throw new InvalidOperationException("No due instant was found; the offset rules are not usable.");
    }

    // Every slot from nine days before to nine days after the instant's local date, in time order. A zone shift is under a day, so a
    // weekly schedule always has a slot on each side.
    private List<DateTimeOffset> Around(DateTimeOffset instant)
    {
        DateTimeOffset utc = instant.ToUniversalTime();
        if (utc < _earliest || utc > _latest)
        {
            throw new ArgumentOutOfRangeException(nameof(instant), instant, "The instant must be from 1970 to 9000.");
        }

        DateOnly today = DateOnly.FromDateTime((utc + OffsetAt(utc)).DateTime);
        List<DateTimeOffset> slots = [];
        for (int day = -9; day <= 9; day++)
        {
            DateOnly date = today.AddDays(day);
            if (Cadence == ReportCadence.Weekly && date.DayOfWeek != DayOfWeek)
            {
                continue;
            }

            slots.Add(SlotFor(date));
        }

        slots.Sort();
        return slots;
    }

    private DateTimeOffset SlotFor(DateOnly date)
    {
        DateTime local = date.ToDateTime(TimeOnly.FromTimeSpan(TimeOfDay));
        DateTimeOffset? best = null;
        for (int index = 0; index < _rules.Length; index++)
        {
            DateTimeOffset candidate = new DateTimeOffset(local, _rules[index].Offset).ToUniversalTime();
            bool afterStart = index == 0 || candidate >= _rules[index].EffectiveFrom;
            bool beforeEnd = index == _rules.Length - 1 || candidate < _rules[index + 1].EffectiveFrom;
            if (afterStart && beforeEnd && (best is null || candidate < best))
            {
                best = candidate;
            }
        }

        if (best is not null)
        {
            return best.Value;
        }

        // The local time was skipped by a forward jump: it is due at the moment the clock jumps.
        for (int index = 1; index < _rules.Length; index++)
        {
            DateTimeOffset before = new DateTimeOffset(local, _rules[index - 1].Offset).ToUniversalTime();
            DateTimeOffset after = new DateTimeOffset(local, _rules[index].Offset).ToUniversalTime();
            if (before >= _rules[index].EffectiveFrom && after < _rules[index].EffectiveFrom)
            {
                return _rules[index].EffectiveFrom;
            }
        }

        throw new InvalidOperationException("The local time cannot be placed on the offset rules.");
    }
}
