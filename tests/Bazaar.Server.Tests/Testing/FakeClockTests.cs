using Bazaar.Testing;

namespace Bazaar.Server.Tests.Testing;

public class FakeClockTests
{
    [Fact]
    public void Starts_at_the_default_instant_in_UTC()
    {
        var clock = new FakeClock();

        Assert.Equal(FakeClock.DefaultStart, clock.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow().Offset);
    }

    [Fact]
    public void Advance_moves_UtcNow_forward_by_exactly_the_given_span()
    {
        var clock = new FakeClock();

        clock.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(FakeClock.DefaultStart + TimeSpan.FromMinutes(90), clock.GetUtcNow());
    }

    [Fact]
    public void SetUtcNow_jumps_to_the_given_instant()
    {
        var clock = new FakeClock();
        var target = new DateTimeOffset(2030, 6, 15, 12, 0, 0, TimeSpan.Zero);

        clock.SetUtcNow(target);

        Assert.Equal(target, clock.GetUtcNow());
    }

    [Fact]
    public void Local_time_zone_is_the_one_given_and_converts_UtcNow()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+5", TimeSpan.FromHours(5), "Test +05", "Test +05");
        var clock = new FakeClock(localTimeZone: zone);

        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone);

        Assert.Equal(zone.Id, clock.LocalTimeZone.Id);
        Assert.Equal(TimeSpan.FromHours(5), local.Offset);
    }

    [Fact]
    public void SetLocalTimeZone_replaces_the_zone()
    {
        var clock = new FakeClock();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-3", TimeSpan.FromHours(-3), "Test -03", "Test -03");

        clock.SetLocalTimeZone(zone);

        Assert.Equal(zone.Id, clock.LocalTimeZone.Id);
    }

    [Fact]
    public void Timer_does_not_fire_before_it_is_due_and_fires_once_when_advanced_past_it()
    {
        var clock = new FakeClock();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, fired);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ToString_names_the_UTC_instant_and_the_local_zone()
    {
        var clock = new FakeClock();

        var text = clock.ToString();

        Assert.Contains("2026-01-01T00:00:00.0000000+00:00", text);
        Assert.Contains(clock.LocalTimeZone.Id, text);
    }
}
