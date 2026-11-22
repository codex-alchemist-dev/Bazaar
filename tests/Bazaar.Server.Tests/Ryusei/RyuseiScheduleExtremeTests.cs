using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// Chaos round 3: the pure next-fire arithmetic at the edges of DateTimeOffset and TimeSpan, across zones with DST. A trigger
/// whose schedule cannot be computed must be refused at creation, never throw out of creation or out of the tick.
/// </summary>
public sealed class RyuseiScheduleExtremeTests
{
    private static readonly TimeSpan _floor = TimeSpan.FromMinutes(5);

    public static TheoryData<string> Zones => ["UTC", "America/New_York", "Europe/London", "Australia/Lord_Howe", "Pacific/Kiritimati", "Pacific/Pago_Pago", "Asia/Kolkata"];

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_next_fire_never_throws_and_is_ordered_for_random_specs(string zoneId)
    {
        var zone = Zone(zoneId);
        var random = new Random(20261119);
        for (var i = 0; i < 4000; i++)
        {
            var anchor = new DateTimeOffset(random.NextInt64(DateTimeOffset.MinValue.UtcTicks + TimeSpan.TicksPerDay, DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerDay), TimeSpan.Zero);
            var every = random.Next(4) switch
            {
                0 => TimeSpan.FromMinutes(random.Next(5, 600)),
                1 => TimeSpan.FromDays(random.Next(1, 400)),
                2 => TimeSpan.FromTicks(random.NextInt64(_floor.Ticks, TimeSpan.MaxValue.Ticks)),
                _ => TimeSpan.FromHours(random.Next(1, 100)),
            };
            var now = new DateTimeOffset(random.NextInt64(DateTimeOffset.UnixEpoch.UtcTicks, new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks), TimeSpan.Zero);
            var spec = new RyuseiIntervalTriggerSpec(every, anchor);

            var fire = RyuseiSchedule.NextFire(spec, now, zone, _floor);

            Assert.True(fire.NextRunAt > now || fire.NextRunAt >= anchor, $"{spec} now {now:O}: next {fire.NextRunAt:O}");
            if (fire.IsDue)
                Assert.True(fire.ScheduledFor <= now);
        }
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void A_daily_interval_moves_forward_across_every_dst_change(string zoneId)
    {
        var zone = Zone(zoneId);
        var anchor = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromDays(1), anchor);
        var previous = DateTimeOffset.MinValue;
        for (var day = 0; day < 800; day++)
        {
            var now = anchor.AddDays(day).AddHours(5);
            var fire = RyuseiSchedule.NextFire(spec, now, zone, _floor);

            Assert.True(fire.IsDue);
            Assert.True(fire.ScheduledFor > previous, $"{zoneId} day {day}: {fire.ScheduledFor:O} not after {previous:O}");
            Assert.True(fire.NextRunAt > now);
            previous = fire.ScheduledFor!.Value;
        }
    }
}
