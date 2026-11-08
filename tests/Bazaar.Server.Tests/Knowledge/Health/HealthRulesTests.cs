using Bazaar.Server.Knowledge;
using Bazaar.Server.Knowledge.Health;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Tests.Knowledge.Health;

public sealed class HealthRulesTests
{
    private static readonly DateOnly _day1 = new(2026, 10, 26);

    private static MetricSample Coverage(string toolchain, DateOnly day, double percent) =>
        new("bazaar", MetricNames.CoverageLinePercent, toolchain, day, percent, $"report:{toolchain}:{day:yyyy-MM-dd}");

    private static IEnumerable<MetricSample> Ci(DateOnly day, int total, int passed) =>
    [
        new("bazaar", MetricNames.CiRunsTotal, string.Empty, day, total, $"ci:{day:yyyy-MM-dd}"),
        new("bazaar", MetricNames.CiRunsPassed, string.Empty, day, passed, $"ci:{day:yyyy-MM-dd}"),
    ];

    private static IReadOnlyList<MetricSample> Failing(DateOnly day) => [.. Ci(day, 2, 1)];

    private static IReadOnlyList<MetricSample> Passing(DateOnly day) => [.. Ci(day, 2, 2)];

    [Fact]
    public void Coverage_drop_of_exactly_the_setting_raises_an_alert()
    {
        var reasons = HealthRules.CoverageDrops([Coverage("dotnet-10", _day1, 80), Coverage("dotnet-10", _day1.AddDays(1), 78)], 2);

        var reason = Assert.Single(reasons);
        Assert.Equal(HealthReasonKind.CoverageDrop, reason.Kind);
        Assert.Contains("dotnet-10", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_drop_below_the_setting_does_not_raise_an_alert()
    {
        var reasons = HealthRules.CoverageDrops([Coverage("dotnet-10", _day1, 80), Coverage("dotnet-10", _day1.AddDays(1), 78.01)], 2);

        Assert.Empty(reasons);
    }

    [Fact]
    public void A_missing_day_is_skipped_so_the_previous_recorded_value_is_used()
    {
        var reasons = HealthRules.CoverageDrops([Coverage("dotnet-10", _day1, 80), Coverage("dotnet-10", _day1.AddDays(2), 75)], 2);

        Assert.Single(reasons);
    }

    [Fact]
    public void A_missing_day_is_not_read_as_a_drop_to_zero()
    {
        var reasons = HealthRules.CoverageDrops([Coverage("dotnet-10", _day1, 80), Coverage("dotnet-10", _day1.AddDays(2), 79)], 2);

        Assert.Empty(reasons);
    }

    [Fact]
    public void Coverage_rise_never_raises_an_alert_even_at_zero_points()
    {
        var reasons = HealthRules.CoverageDrops([Coverage("dotnet-10", _day1, 80), Coverage("dotnet-10", _day1.AddDays(1), 85)], 0);

        Assert.Empty(reasons);
    }

    [Fact]
    public void Coverage_is_compared_only_within_one_toolchain()
    {
        var samples = new[]
        {
            Coverage("dotnet-10", _day1, 90),
            Coverage("dotnet-10", _day1.AddDays(1), 89),
            Coverage("node-22", _day1, 80),
            Coverage("node-22", _day1.AddDays(1), 60),
        };

        var reason = Assert.Single(HealthRules.CoverageDrops(samples, 2));

        Assert.Contains("node-22", reason.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-10", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_coverage_day_cannot_show_a_drop()
    {
        Assert.Empty(HealthRules.CoverageDrops([Coverage("dotnet-10", _day1, 10)], 2));
        Assert.Empty(HealthRules.CoverageDrops([], 2));
    }

    [Fact]
    public void Three_failing_days_in_a_row_meet_a_streak_of_three()
    {
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(1))).Concat(Failing(_day1.AddDays(2))).ToList();

        var reason = Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(2), 3));

        Assert.Equal(HealthReasonKind.BuildFailureStreak, reason.Kind);
        Assert.Contains("3 consecutive", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_failing_days_do_not_meet_a_streak_of_three()
    {
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(1))).ToList();

        Assert.Empty(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(1), 3));
    }

    [Fact]
    public void A_fully_passing_day_ends_the_streak()
    {
        // Newest first: fail, then a fully passing day, then two failures older than the pass. Only the newest failure counts.
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(1))).Concat(Passing(_day1.AddDays(2))).Concat(Failing(_day1.AddDays(3))).ToList();

        Assert.Empty(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(3), 2));
        var reason = Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(3), 1));
        Assert.Contains("1 consecutive", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_day_with_one_failed_run_out_of_several_counts_as_a_failing_day()
    {
        var samples = Ci(_day1, 5, 4).Concat(Ci(_day1.AddDays(1), 3, 2)).ToList();

        Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(1), 2));
    }

    [Fact]
    public void A_day_with_no_sample_is_a_gap_that_does_not_count_as_a_failure()
    {
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(2))).ToList();

        Assert.Empty(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(2), 3));
    }

    [Fact]
    public void A_gap_between_failures_is_bridged_and_the_message_says_so()
    {
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(2))).Concat(Failing(_day1.AddDays(3))).ToList();

        var reason = Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(3), 3));

        Assert.Contains("1 day(s) with no CI run inside the streak were skipped", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gap_before_the_newest_failure_is_not_reported_as_bridged()
    {
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(1))).ToList();

        var reason = Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(3), 2));

        Assert.DoesNotContain("skipped", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_gap_after_the_last_run_does_not_reset_the_streak()
    {
        var samples = Failing(_day1).Concat(Failing(_day1.AddDays(1))).ToList();

        Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(5), 2));
    }

    [Fact]
    public void A_day_with_zero_runs_is_treated_as_a_gap()
    {
        var samples = Failing(_day1).Concat(Ci(_day1.AddDays(1), 0, 0)).Concat(Failing(_day1.AddDays(2))).ToList();

        Assert.Single(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(2), 2));
    }

    [Fact]
    public void Samples_outside_the_window_are_ignored()
    {
        var samples = Failing(_day1.AddDays(-5)).Concat(Failing(_day1.AddDays(-6))).Concat(Failing(_day1.AddDays(-7))).ToList();

        Assert.Empty(HealthRules.BuildFailureStreak(samples, _day1, _day1.AddDays(2), 2));
    }

    [Fact]
    public void Streak_threshold_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HealthRules.BuildFailureStreak([], _day1, _day1, 0));
    }

    [Fact]
    public void Thresholds_read_the_shipped_defaults_from_settings()
    {
        Assert.Equal(KnowledgeSettings.HealthCoverageDropPoints.Default, HealthThresholds.Defaults.CoverageDropPoints);
        Assert.Equal(KnowledgeSettings.HealthBuildFailureStreak.Default, HealthThresholds.Defaults.BuildFailureStreak);
        Assert.Equal(2, HealthThresholds.Defaults.CoverageDropPoints);
        Assert.Equal(3, HealthThresholds.Defaults.BuildFailureStreak);
    }
}
