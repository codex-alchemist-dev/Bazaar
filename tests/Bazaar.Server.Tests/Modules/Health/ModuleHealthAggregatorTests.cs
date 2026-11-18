using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Tests.Modules.Health;

/// <summary>Module health aggregation: worst-of status, the green, amber and red light, and the score caps (doc 46 sections 8 and 12). Pure.</summary>
public sealed class ModuleHealthAggregatorTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static ModuleHealthCheck Check(ModuleHealthStatus status) => new(status, status.ToString(), _at);

    // Worst-of through the public path: one module holding the checks.
    private static ModuleHealthStatus Worst(IReadOnlyList<ModuleHealthCheck> checks) =>
        ModuleHealthAggregator.Aggregate([new ModuleHealthReport("m", checks)]).Status;

    private static ModuleHealthReport Module(string id, params ModuleHealthStatus[] statuses) =>
        new(id, statuses.Select(Check).ToList());

    [Fact]
    public void Worst_of_all_healthy_parts_is_healthy_and_green()
    {
        var aggregate = ModuleHealthAggregator.Aggregate([Module("gc", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy)]);

        Assert.Equal(ModuleHealthStatus.Healthy, aggregate.Status);
        Assert.Equal(ModuleHealthLight.Green, aggregate.Light);
        Assert.Equal(100, aggregate.Score);
    }

    [Fact]
    public void One_failing_part_makes_the_whole_installation_failing_and_red()
    {
        var aggregate = ModuleHealthAggregator.Aggregate(
        [
            Module("gc", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy),
            Module("postgresql", ModuleHealthStatus.Healthy, ModuleHealthStatus.Failing),
        ]);

        Assert.Equal(ModuleHealthStatus.Failing, aggregate.Status);
        Assert.Equal(ModuleHealthLight.Red, aggregate.Light);
    }

    [Theory]
    [InlineData(ModuleHealthStatus.Healthy, ModuleHealthStatus.Degraded, ModuleHealthStatus.Degraded)]
    [InlineData(ModuleHealthStatus.Degraded, ModuleHealthStatus.Unknown, ModuleHealthStatus.Unknown)]
    [InlineData(ModuleHealthStatus.Unknown, ModuleHealthStatus.Failing, ModuleHealthStatus.Failing)]
    [InlineData(ModuleHealthStatus.Failing, ModuleHealthStatus.Degraded, ModuleHealthStatus.Failing)]
    public void Overall_status_is_the_worst_of_the_parts_in_severity_order(ModuleHealthStatus first, ModuleHealthStatus second, ModuleHealthStatus expected)
    {
        Assert.Equal(expected, Worst([Check(first), Check(second)]));
        Assert.Equal(expected, Worst([Check(second), Check(first)]));
    }

    [Fact]
    public void Empty_check_list_is_unknown_and_never_green()
    {
        var aggregate = ModuleHealthAggregator.Aggregate([]);

        Assert.Equal(ModuleHealthStatus.Unknown, aggregate.Status);
        Assert.Equal(ModuleHealthLight.Amber, aggregate.Light);
        Assert.Equal(0, aggregate.Score);
    }

    [Fact]
    public void Module_with_no_checks_is_unknown_in_its_rollup()
    {
        var aggregate = ModuleHealthAggregator.Aggregate([new ModuleHealthReport("empty", [])]);

        Assert.Equal(ModuleHealthStatus.Unknown, aggregate.Modules.Single().Status);
        Assert.Equal(ModuleHealthLight.Amber, aggregate.Modules.Single().Light);
    }

    [Theory]
    [InlineData(ModuleHealthStatus.Healthy, ModuleHealthLight.Green)]
    [InlineData(ModuleHealthStatus.Degraded, ModuleHealthLight.Amber)]
    [InlineData(ModuleHealthStatus.Unknown, ModuleHealthLight.Amber)]
    [InlineData(ModuleHealthStatus.Failing, ModuleHealthLight.Red)]
    public void Status_maps_to_the_light_the_monitor_shows(ModuleHealthStatus status, ModuleHealthLight expected)
    {
        Assert.Equal(expected, ModuleHealthAggregator.LightOf(status));
    }

    [Fact]
    public void Unknown_check_is_amber_even_when_every_other_check_is_healthy()
    {
        var aggregate = ModuleHealthAggregator.Aggregate([Module("kanban", ModuleHealthStatus.Healthy, ModuleHealthStatus.Unknown)]);

        Assert.Equal(ModuleHealthLight.Amber, aggregate.Light);
        Assert.NotEqual(ModuleHealthStatus.Healthy, aggregate.Status);
    }

    [Fact]
    public void Score_is_the_healthy_share_rounded_down()
    {
        // 2 of 3 healthy is 66.66..., which rounds down to 66. The rest are degraded, so no cap applies.
        var aggregate = ModuleHealthAggregator.Aggregate(
            [Module("gc", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy, ModuleHealthStatus.Degraded)]);

        Assert.Equal(66, aggregate.Score);
    }

    [Fact]
    public void Any_failing_check_caps_the_score_at_49()
    {
        var aggregate = ModuleHealthAggregator.Aggregate(
            [Module("gc", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy, ModuleHealthStatus.Failing)]);

        Assert.Equal(ModuleHealthAggregator.FailingScoreCap, aggregate.Score);
        Assert.Equal(49, aggregate.Score);
    }

    [Fact]
    public void Any_unknown_check_caps_the_score_at_70()
    {
        var aggregate = ModuleHealthAggregator.Aggregate(
            [Module("gc", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy, ModuleHealthStatus.Unknown)]);

        Assert.Equal(ModuleHealthAggregator.UnknownScoreCap, aggregate.Score);
        Assert.Equal(70, aggregate.Score);
    }

    [Fact]
    public void Score_cap_never_turns_a_low_score_into_a_higher_one()
    {
        // All failing: the raw share is 0, and the cap is a ceiling only.
        var aggregate = ModuleHealthAggregator.Aggregate([Module("gc", ModuleHealthStatus.Failing)]);

        Assert.Equal(0, aggregate.Score);
        Assert.Equal(ModuleHealthLight.Red, aggregate.Light);
    }

    [Fact]
    public void Score_cannot_make_a_red_installation_green()
    {
        var aggregate = ModuleHealthAggregator.Aggregate(
            [Module("gc", Enumerable.Repeat(ModuleHealthStatus.Healthy, 50).Append(ModuleHealthStatus.Failing).ToArray())]);

        Assert.True(aggregate.Score <= ModuleHealthAggregator.FailingScoreCap);
        Assert.Equal(ModuleHealthLight.Red, aggregate.Light);
    }

    [Fact]
    public void Rollups_keep_each_module_worst_status_in_input_order()
    {
        var aggregate = ModuleHealthAggregator.Aggregate(
        [
            Module("gc", ModuleHealthStatus.Healthy),
            Module("postgresql", ModuleHealthStatus.Degraded, ModuleHealthStatus.Healthy),
        ]);

        Assert.Collection(
            aggregate.Modules,
            first =>
            {
                Assert.Equal("gc", first.ModuleId);
                Assert.Equal(ModuleHealthStatus.Healthy, first.Status);
            },
            second =>
            {
                Assert.Equal("postgresql", second.ModuleId);
                Assert.Equal(ModuleHealthStatus.Degraded, second.Status);
                Assert.Equal(ModuleHealthLight.Amber, second.Light);
            });
    }

    [Fact]
    public void Duplicate_module_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ModuleHealthAggregator.Aggregate(
            [Module("gc", ModuleHealthStatus.Healthy), Module("gc", ModuleHealthStatus.Healthy)]));
    }

    [Fact]
    public void Blank_module_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ModuleHealthAggregator.Aggregate([Module(" ", ModuleHealthStatus.Healthy)]));
    }
}
