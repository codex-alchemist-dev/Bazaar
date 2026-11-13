using Bazaar.Server.Modules.Updates;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Updates;

/// <summary>Defer aging (doc 42 section 10.2): how long a deferred update has waited, against the warning threshold.</summary>
public sealed class DeferAgingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static DeferAging Make(out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Now);
        return new DeferAging(clock);
    }

    [Fact]
    public void Setting_key_and_default_match_the_design()
    {
        Assert.Equal("deps.update.deferWarnDays", DeferAging.DeferWarnDaysSettingKey);
        Assert.Equal(30, DeferAging.DefaultDeferWarnDays);
    }

    [Fact]
    public void A_deferral_started_now_has_no_age_and_is_not_aging()
    {
        var aging = Make(out _);

        var report = aging.Measure(Now, DeferAging.DefaultDeferWarnDays);

        Assert.Equal(new DeferAgeReport(0, false), report);
    }

    [Fact]
    public void Exactly_the_threshold_is_not_yet_aging()
    {
        var aging = Make(out _);

        var report = aging.Measure(Now.AddDays(-30), 30);

        Assert.Equal(new DeferAgeReport(30, false), report);
    }

    [Fact]
    public void One_minute_past_the_threshold_is_aging()
    {
        var aging = Make(out _);

        var report = aging.Measure(Now.AddDays(-30).AddMinutes(-1), 30);

        Assert.Equal(new DeferAgeReport(30, true), report);
    }

    [Fact]
    public void Age_is_whole_days_rounded_down()
    {
        var aging = Make(out _);

        var report = aging.Measure(Now.AddDays(-7).AddHours(-23), 30);

        Assert.Equal(7, report.AgeDays);
        Assert.False(report.IsAging);
    }

    [Fact]
    public void Age_follows_the_injected_clock()
    {
        var aging = Make(out var clock);
        var since = Now;

        clock.Advance(TimeSpan.FromDays(45));

        Assert.Equal(new DeferAgeReport(45, true), aging.Measure(since, 30));
    }

    [Fact]
    public void A_deferral_time_in_the_future_reports_zero_age()
    {
        var aging = Make(out _);

        var report = aging.Measure(Now.AddDays(3), 30);

        Assert.Equal(new DeferAgeReport(0, false), report);
    }

    [Fact]
    public void A_zero_threshold_flags_any_wait_longer_than_zero()
    {
        var aging = Make(out _);

        Assert.True(aging.Measure(Now.AddHours(-1), 0).IsAging);
        Assert.False(aging.Measure(Now, 0).IsAging);
    }

    [Fact]
    public void Negative_threshold_is_a_programmer_error_not_a_normal_path()
    {
        var aging = Make(out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => aging.Measure(Now, -1));
    }
}
