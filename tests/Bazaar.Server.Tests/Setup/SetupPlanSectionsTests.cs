using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The section registry: every plan leaf has exactly one owner, defaults are not requested, and the message names steps only.</summary>
public sealed class SetupPlanSectionsTests
{
    private static SetupPlan ValidPlan() => SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

    [Fact]
    public void Every_leaf_of_a_plan_belongs_to_exactly_one_section()
    {
        var leaves = SetupPlanCanonicalizer.Leaves(SetupPlanCanonicalizer.ToNode(ValidPlan())).Keys.Where(path => path != "schemaVersion");

        foreach (var leaf in leaves)
        {
            var owners = SetupPlanSections.All.Where(section => section.PlanPaths.Any(path => leaf == path || leaf.StartsWith(path + ".", StringComparison.Ordinal))).ToList();
            Assert.True(owners.Count == 1, $"plan leaf '{leaf}' has {owners.Count} owning sections");
        }
    }

    [Fact]
    public void Section_ids_are_unique_and_known()
    {
        Assert.Equal(SetupPlanSections.All.Count, SetupPlanSections.All.Select(section => section.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(SetupPlanSections.All, section => Assert.True(SetupPlanSections.IsKnown(section.Id)));
        Assert.False(SetupPlanSections.IsKnown("nope"));
    }

    [Fact]
    public void An_untouched_default_plan_asks_for_nothing()
    {
        Assert.Empty(SetupPlanSections.Requested(new SetupPlan()));
    }

    [Fact]
    public void A_state_that_already_holds_the_plan_asks_for_nothing()
    {
        var plan = ValidPlan();

        var requested = SetupPlanSections.Requested(plan, SetupStateSnapshot.FromNode(SetupPlanCanonicalizer.ToNode(plan)));

        Assert.Empty(requested);
    }

    [Fact]
    public void Describe_names_the_sections_and_doc_steps_and_no_value()
    {
        var text = SetupPlanSections.Describe(SetupPlanSections.ByIds(["owner", "database", "settings"]));

        Assert.Equal("database (doc 45 steps 3, 4), owner (doc 45 step 6), settings", text);
    }

    [Fact]
    public void A_step_declares_no_sections_by_default()
    {
        ISetupStep step = new DefaultStep();

        Assert.Empty(step.Covers);
    }

    private sealed class DefaultStep : ISetupStep
    {
        public string Id => "x";

        public int Order => 1;

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupStepResult.Ok);
    }
}
