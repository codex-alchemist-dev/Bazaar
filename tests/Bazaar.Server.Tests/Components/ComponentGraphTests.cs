using Bazaar.Core;

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
    public void A_soft_dependency_does_not_set_start_order()
    {
        // "aaa" softly depends on "zzz". A soft edge would start "zzz" first. Id order must win, so "aaa" starts first.
        var ordered = ComponentGraph.Order([new ComponentManifest("aaa", "0.1.0", ComponentKind.Feature, [], [], ["zzz"]), M("zzz")]);

        Assert.Equal(["aaa", "zzz"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void Mutual_soft_links_are_allowed_and_ordered_by_id()
    {
        var ordered = ComponentGraph.Order(
            [new ComponentManifest("a", "0.1.0", ComponentKind.Feature, ["c"], [], ["b"]),
             new ComponentManifest("b", "0.1.0", ComponentKind.Feature, ["c"], [], ["a"]),
             M("c")]);

        Assert.Equal(["c", "a", "b"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void A_missing_soft_dependency_is_not_an_error()
    {
        var ordered = ComponentGraph.Order([new ComponentManifest("kanban", "0.1.0", ComponentKind.Feature, [], [], ["gc"])]);

        Assert.Equal(["kanban"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void A_hard_cycle_is_rejected_even_when_soft_links_exist()
    {
        var ex = Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order(
            [new ComponentManifest("a", "0.1.0", ComponentKind.Feature, ["b"], [], ["c"]),
             new ComponentManifest("b", "0.1.0", ComponentKind.Feature, ["a"], [])]));

        Assert.Contains("cycle", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void A_failed_hard_target_blocks_its_dependents_through_other_hard_edges()
    {
        var impact = ComponentGraph.Impact([M("db"), M("vault", "db"), M("agents", "vault"), M("mail")], "db");

        Assert.Equal(["agents", "vault"], impact.Blocked);
        Assert.Empty(impact.Degraded);
    }

    [Fact]
    public void A_soft_dependent_is_degraded_not_blocked()
    {
        var impact = ComponentGraph.Impact(
            [M("search"), new ComponentManifest("agents", "0.1.0", ComponentKind.Feature, [], [], ["search"])], "search");

        Assert.Empty(impact.Blocked);
        Assert.Equal(["agents"], impact.Degraded);
    }

    [Fact]
    public void A_soft_link_to_a_blocked_component_degrades_and_mutual_soft_links_are_fine()
    {
        var impact = ComponentGraph.Impact(
            [M("db"),
             M("vault", "db"),
             new ComponentManifest("a", "0.1.0", ComponentKind.Feature, [], [], ["vault", "b"]),
             new ComponentManifest("b", "0.1.0", ComponentKind.Feature, [], [], ["a"])],
            "db");

        Assert.Equal(["vault"], impact.Blocked);
        Assert.Equal(["a"], impact.Degraded);
    }

    [Fact]
    public void Impact_of_an_unknown_component_is_refused()
    {
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Impact([M("a")], "zzz"));
    }
}
