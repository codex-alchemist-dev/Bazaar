using Bazaar.Server.Components;

namespace Bazaar.Server.Tests.Components;

public class ComponentGraphTests
{
    private static ComponentManifest M(string id, params string[] dependsOn) =>
        new(id, "0.1.0", ComponentKind.Feature, dependsOn, []);

    [Fact]
    public void Dependencies_come_before_their_dependents()
    {
        var ordered = ComponentGraph.Order([M("vault", "gc"), M("gc"), M("agents", "vault", "gc")]);

        Assert.Equal(["gc", "vault", "agents"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void Independent_components_are_ordered_by_id_so_every_start_matches()
    {
        var ordered = ComponentGraph.Order([M("skimmer"), M("kanban"), M("calendar")]);

        Assert.Equal(["calendar", "kanban", "skimmer"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void Duplicate_id_is_rejected()
    {
        var ex = Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("kanban"), M("kanban")]));

        Assert.Contains("Duplicate component id 'kanban'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_dependency_is_named()
    {
        var ex = Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("kanban", "missing")]));

        Assert.Contains("'kanban' depends on unknown component 'missing'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_dependency_is_rejected()
    {
        var ex = Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("kanban", "kanban")]));

        Assert.Contains("depends on itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cycle_names_every_component_in_it_and_nothing_outside_it()
    {
        var ex = Assert.Throws<ComponentGraphException>(() =>
            ComponentGraph.Order([M("a", "b"), M("b", "a"), M("c")]));

        Assert.Contains("among: a, b.", ex.Message, StringComparison.Ordinal);
    }
}
