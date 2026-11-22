using Bazaar.Core;
using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Modules.Tests.Health;

/// <summary>Health spreading along hard and soft dependency edges (doc 46 section 10.1, components section 4.1). Pure.</summary>
public sealed class ModuleHealthPropagatorTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static ComponentManifest M(string id, string[]? hard = null, string[]? soft = null) =>
        new(id, "0.1.0", ComponentKind.Feature, hard ?? [], [], soft);

    private static ModuleHealthReport R(string id, ModuleHealthStatus status) =>
        new(id, [new ModuleHealthCheck(status, status.ToString(), _at)]);

    private static Dictionary<string, ModuleHealthStatus> Run(ComponentManifest[] manifests, params ModuleHealthReport[] reports) =>
        ModuleHealthPropagator.Propagate(manifests, reports).ToDictionary(r => r.ModuleId, r => r.Status);

    [Fact]
    public void A_failing_hard_target_blocks_every_hard_dependent_through_the_chain()
    {
        var result = Run(
            [M("db"), M("vault", ["db"]), M("agents", ["vault"])],
            R("db", ModuleHealthStatus.Failing), R("vault", ModuleHealthStatus.Healthy), R("agents", ModuleHealthStatus.Healthy));

        Assert.Equal(ModuleHealthStatus.Failing, result["vault"]);
        Assert.Equal(ModuleHealthStatus.Failing, result["agents"]);
    }

    [Fact]
    public void A_soft_target_that_is_failing_only_degrades()
    {
        var result = Run(
            [M("search"), M("agents", soft: ["search"])],
            R("search", ModuleHealthStatus.Failing), R("agents", ModuleHealthStatus.Healthy));

        Assert.Equal(ModuleHealthStatus.Degraded, result["agents"]);
    }

    [Fact]
    public void An_unknown_hard_target_never_leaves_a_dependent_healthy()
    {
        var result = Run([M("db"), M("vault", ["db"])], R("db", ModuleHealthStatus.Unknown), R("vault", ModuleHealthStatus.Healthy));

        Assert.Equal(ModuleHealthStatus.Unknown, result["vault"]);
    }

    [Fact]
    public void A_hard_target_with_no_report_counts_as_unknown()
    {
        var result = Run([M("db"), M("vault", ["db"])], R("vault", ModuleHealthStatus.Healthy));

        Assert.Equal(ModuleHealthStatus.Unknown, result["vault"]);
    }

    [Fact]
    public void A_degraded_hard_target_degrades_its_dependent()
    {
        var result = Run([M("db"), M("vault", ["db"])], R("db", ModuleHealthStatus.Degraded), R("vault", ModuleHealthStatus.Healthy));

        Assert.Equal(ModuleHealthStatus.Degraded, result["vault"]);
    }

    [Fact]
    public void A_modules_own_worse_status_stands()
    {
        var result = Run([M("db"), M("vault", ["db"])], R("db", ModuleHealthStatus.Degraded), R("vault", ModuleHealthStatus.Failing));

        Assert.Equal(ModuleHealthStatus.Failing, result["vault"]);
    }

    [Fact]
    public void Mutual_soft_links_with_a_healthy_base_stay_healthy()
    {
        var result = Run(
            [M("c"), M("a", ["c"], ["b"]), M("b", ["c"], ["a"])],
            R("c", ModuleHealthStatus.Healthy), R("a", ModuleHealthStatus.Healthy), R("b", ModuleHealthStatus.Healthy));

        Assert.All(result.Values, s => Assert.Equal(ModuleHealthStatus.Healthy, s));
    }

    [Fact]
    public void Mutual_soft_links_degrade_each_other_when_one_side_fails()
    {
        var result = Run(
            [M("c"), M("a", ["c"], ["b"]), M("b", ["c"], ["a"])],
            R("c", ModuleHealthStatus.Healthy), R("a", ModuleHealthStatus.Healthy), R("b", ModuleHealthStatus.Failing));

        Assert.Equal(ModuleHealthStatus.Degraded, result["a"]);
        Assert.Equal(ModuleHealthStatus.Failing, result["b"]);
    }

    [Fact]
    public void A_hard_cycle_is_refused()
    {
        Assert.Throws<ComponentGraphException>(() =>
            ModuleHealthPropagator.Propagate([M("a", ["b"]), M("b", ["a"])], [R("a", ModuleHealthStatus.Healthy)]));
    }

    [Fact]
    public void A_reported_module_missing_from_the_manifests_keeps_its_own_status()
    {
        var result = Run([M("a")], R("a", ModuleHealthStatus.Healthy), R("ghost", ModuleHealthStatus.Degraded));

        Assert.Equal(ModuleHealthStatus.Degraded, result["ghost"]);
    }
}
