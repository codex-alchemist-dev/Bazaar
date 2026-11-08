namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// When a timer or interval is next due. <see cref="NextRunAt"/> is the first occurrence after <c>now</c>.
/// If <see cref="IsDue"/> is true, the trigger fires once for <see cref="ScheduledFor"/>, the latest due occurrence.
/// <see cref="MissedCount"/> counts the earlier due occurrences that coalesce into that one fire (doc 38, Missed fires).
/// </summary>
public sealed record RyuseiFireSchedule(DateTimeOffset NextRunAt, bool IsDue, DateTimeOffset? ScheduledFor, long MissedCount);

/// <summary>
/// Next-fire arithmetic for timer and interval triggers (doc 38, Trigger types and Missed fires). Pure: no clock, no
/// database. The caller passes <c>now</c>, the agent's time zone and the last scheduled occurrence it handled.
/// </summary>
public static class RyuseiSchedule
{
    /// <summary>
    /// A timer fires once. If its time has passed, it is due now and fires once, with no missed count.
    /// </summary>
    public static RyuseiFireSchedule NextFire(RyuseiTimerTriggerSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.FireAt <= now
            ? new RyuseiFireSchedule(spec.FireAt, IsDue: true, ScheduledFor: spec.FireAt, MissedCount: 0)
            : new RyuseiFireSchedule(spec.FireAt, IsDue: false, ScheduledFor: null, MissedCount: 0);
    }

    /// <summary>
    /// The next run of an interval. Occurrences are anchored at <see cref="RyuseiIntervalTriggerSpec.Anchor"/>.
    /// Whole-day intervals step in the agent's wall-clock time, so a daily interval keeps its local time across DST.
    /// Shorter intervals step in absolute time. Due occurrences after <paramref name="lastScheduledFor"/> coalesce into
    /// one fire: the latest one, with the others counted in <see cref="RyuseiFireSchedule.MissedCount"/>. Null
    /// <paramref name="lastScheduledFor"/> means nothing has fired yet.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The interval is shorter than the effective floor. Creation checks this with <see cref="CheckIntervalFloor"/> first.
    /// </exception>
    public static RyuseiFireSchedule NextFire(
        RyuseiIntervalTriggerSpec spec,
        DateTimeOffset now,
        TimeZoneInfo agentZone,
        TimeSpan minInterval,
        DateTimeOffset? lastScheduledFor = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(agentZone);

        var floorDecision = CheckIntervalFloor(spec.Every, minInterval);
        if (!floorDecision.IsAllowed)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), spec.Every, "The interval is shorter than the RyuseiFuda floor.");
        }

        var grid = new OccurrenceGrid(spec.Anchor, spec.Every, agentZone);
        var latestDue = grid.LatestIndexAtOrBefore(now);
        var firstUndone = lastScheduledFor is null ? 0 : grid.LatestIndexAtOrBefore(lastScheduledFor.Value) + 1;
        var dueCount = latestDue - firstUndone + 1;
        var nextRunAt = grid.Occurrence(latestDue + 1);

        return dueCount > 0
            ? new RyuseiFireSchedule(nextRunAt, IsDue: true, ScheduledFor: grid.Occurrence(latestDue), MissedCount: dueCount - 1)
            : new RyuseiFireSchedule(nextRunAt, IsDue: false, ScheduledFor: null, MissedCount: 0);
    }

    /// <summary>
    /// Checks an interval against the effective floor. The floor is the larger of the configured minimum
    /// (<c>ryusei.min_interval</c>, default PT5M) and the hard floor of PT1M. Settings can raise the floor, never lower it.
    /// </summary>
    public static RyuseiDecision CheckIntervalFloor(TimeSpan every, TimeSpan minInterval)
    {
        var effective = EffectiveIntervalFloor(minInterval);
        return every < effective ? RyuseiDecision.Refuse(RyuseiRefusal.IntervalBelowFloor) : RyuseiDecision.Allow;
    }

    /// <summary>The effective interval floor for a configured minimum: never below <see cref="RyuseiDefaults.IntervalFloor"/>.</summary>
    public static TimeSpan EffectiveIntervalFloor(TimeSpan minInterval) =>
        minInterval > RyuseiDefaults.IntervalFloor ? minInterval : RyuseiDefaults.IntervalFloor;

    /// <summary>
    /// Occurrences are <c>anchor + k * every</c> for k = 0, 1, 2 and so on. For whole-day intervals, k steps the local
    /// calendar day in the agent's zone, and a local time that does not exist (spring-forward gap) moves forward by the gap.
    /// </summary>
    private sealed class OccurrenceGrid
    {
        private readonly DateTimeOffset _anchor;
        private readonly TimeSpan _every;
        private readonly TimeZoneInfo _zone;
        private readonly DateTime _anchorLocal;
        private readonly int _wallDays;

        public OccurrenceGrid(DateTimeOffset anchor, TimeSpan every, TimeZoneInfo zone)
        {
            _anchor = anchor;
            _every = every;
            _zone = zone;
            _anchorLocal = TimeZoneInfo.ConvertTime(anchor, zone).DateTime;

            // Whole days (24 h or more, exact multiples) use wall-clock stepping. Anything shorter is absolute.
            _wallDays = every.Ticks % TimeSpan.TicksPerDay == 0 ? (int)(every.Ticks / TimeSpan.TicksPerDay) : 0;
        }

        public DateTimeOffset Occurrence(long k) =>
            _wallDays > 0
                ? ToInstant(_anchorLocal.AddDays((double)k * _wallDays))
                : _anchor.AddTicks(checked(k * _every.Ticks));

        /// <summary>The largest k with Occurrence(k) at or before <paramref name="instant"/>, or -1 when none has happened yet.</summary>
        public long LatestIndexAtOrBefore(DateTimeOffset instant)
        {
            if (instant < Occurrence(0))
            {
                return -1;
            }

            var elapsed = instant - Occurrence(0);
            var k = _wallDays > 0
                ? (long)Math.Floor(elapsed.TotalDays / _wallDays)
                : elapsed.Ticks / _every.Ticks;

            // The estimate can be off by one day across DST, so step to the exact answer.
            while (k > 0 && Occurrence(k) > instant)
            {
                k--;
            }

            while (Occurrence(k + 1) <= instant)
            {
                k++;
            }

            return k;
        }

        private DateTimeOffset ToInstant(DateTime local)
        {
            DateTime utc;
            if (_zone.IsInvalidTime(local))
            {
                // Spring-forward gap: use the offset in force before the gap, so the wall time moves forward by the gap.
                utc = DateTime.SpecifyKind(local - _zone.GetUtcOffset(local.AddDays(-1)), DateTimeKind.Utc);
            }
            else
            {
                utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);
            }

            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
    }
}
