namespace Bazaar.Kodo.SelfChecks;

/// <summary>
/// When a self-check is due (doc 46 section 17.2): a fixed interval counted from a UTC anchor. Daily, weekly, monthly and quarterly
/// checks are all intervals (a month is 30 days and a quarter is 90 days, see 17.6). Pure and deterministic: no clock is read, and the
/// report schedule of 16.10 is not reused because it is daily and weekly only, with local-time rules a self-check does not need.
/// </summary>
public sealed class SelfCheckSchedule
{
    /// <summary>The shortest interval allowed.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    /// <summary>The longest interval allowed.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(400);

    /// <summary>Creates a schedule. Throws <see cref="ArgumentException"/> for an anchor not in UTC or an interval out of range.</summary>
    public SelfCheckSchedule(DateTimeOffset anchor, TimeSpan interval)
    {
        if (anchor.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The anchor must be given in UTC.", nameof(anchor));
        }

        if (interval < MinInterval || interval > MaxInterval || interval.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The interval must be from 1 hour to 400 days, in whole seconds.", nameof(interval));
        }

        Anchor = anchor;
        Interval = interval;
    }

    /// <summary>The first due instant. Every later one is a whole number of intervals after it.</summary>
    public DateTimeOffset Anchor { get; }

    /// <summary>The time between due instants.</summary>
    public TimeSpan Interval { get; }

    /// <summary>The first due instant strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset NextDue(DateTimeOffset after)
    {
        if (after < Anchor)
        {
            return Anchor;
        }

        long steps = (after - Anchor).Ticks / Interval.Ticks;
        return Anchor.AddTicks((steps + 1) * Interval.Ticks);
    }

    /// <summary>The last due instant at or before <paramref name="instant"/>, or null when the anchor is still in the future.</summary>
    public DateTimeOffset? PreviousDue(DateTimeOffset instant)
    {
        if (instant < Anchor)
        {
            return null;
        }

        long steps = (instant - Anchor).Ticks / Interval.Ticks;
        return Anchor.AddTicks(steps * Interval.Ticks);
    }
}
