using Bazaar.Server.Components;
using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Tests.Data.Migrations;

/// <summary>Namespace discovery, parsing, ordering and planning. Pure logic: no database needed (components design section 5).</summary>
public sealed class MigrationNamespaceTests
{
    private static ComponentManifest Component(string id, params string[] dependsOn) =>
        new(id, "0.1.0", ComponentKind.Feature, dependsOn, []);

    private static MigrationFile Mig(string ns, int version, string name = "f") => new(ns, version, name, $"SELECT {version}");

    [Theory]
    [InlineData("common")]
    [InlineData("kanban")]
    [InlineData("work_life")]
    [InlineData("gate2")]
    public void Valid_namespace_names_are_accepted(string name) => Assert.True(MigrationNamespaces.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData("Kanban")]
    [InlineData("1kanban")]
    [InlineData("_kanban")]
    [InlineData("work-life")]
    [InlineData("work life")]
    [InlineData("work.life")]
    public void Bad_namespace_names_are_rejected(string name) => Assert.False(MigrationNamespaces.IsValid(name));

    [Fact]
    public void A_bad_namespace_folder_is_refused_before_anything_runs()
    {
        var source = new InMemoryMigrationSource([Mig("Bad-Name", 1)]);

        var ex = Assert.Throws<MigrationException>(() => MigrationRunner.Plan(source, "linux", []));
        Assert.Contains("Bad-Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Component_namespace_is_its_id_with_underscores()
    {
        Assert.Equal("work_life", Component("work-life").MigrationNamespace);
        Assert.Equal("kanban", Component("kanban").MigrationNamespace);
    }

    [Fact]
    public void Common_applies_first_then_components_in_dependency_order_then_the_rest_by_name()
    {
        var namespaces = new[] { "zeta", "common", "linux", "vault", "calendar", "kanban" };
        var components = new[] { Component("kanban"), Component("vault", "kanban"), Component("calendar") };

        var ordered = MigrationNamespaces.Order(namespaces, components);

        Assert.Equal(["common", "calendar", "kanban", "vault", "linux", "zeta"], ordered);
    }

    [Fact]
    public void A_component_without_a_folder_is_skipped_and_an_unknown_folder_still_applies()
    {
        var ordered = MigrationNamespaces.Order(["common", "extra"], [Component("kanban")]);

        Assert.Equal(["common", "extra"], ordered);
    }

    [Fact]
    public void Ordering_is_the_same_however_the_inputs_are_listed()
    {
        var components = new[] { Component("skimmer"), Component("kanban"), Component("calendar") };
        var first = MigrationNamespaces.Order(["common", "kanban", "skimmer", "calendar", "linux"], components);
        var second = MigrationNamespaces.Order(["linux", "calendar", "skimmer", "kanban", "common"], [Component("calendar"), Component("kanban"), Component("skimmer")]);

        Assert.Equal(first, second);
        Assert.Equal(["common", "calendar", "kanban", "skimmer", "linux"], first);
    }

    [Fact]
    public void Two_components_mapping_to_one_namespace_are_refused()
    {
        var ex = Assert.Throws<MigrationException>(() => MigrationNamespaces.Order(["common"], [Component("work-life"), Component("work_life")]));
        Assert.Contains("work_life", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_component_cannot_claim_the_shared_common_namespace()
    {
        Assert.Throws<MigrationException>(() => MigrationNamespaces.Order(["common"], [Component("common")]));
    }

    [Fact]
    public void A_component_graph_cycle_fails_the_order_with_the_ids_named()
    {
        var ex = Assert.Throws<ComponentGraphException>(() => MigrationNamespaces.Order(["common"], [Component("a", "b"), Component("b", "a")]));
        Assert.Contains("a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_takes_common_and_the_component_namespaces_and_only_this_providers_folder()
    {
        var source = new InMemoryMigrationSource(
        [
            Mig("common", 1), Mig("common", 2),
            Mig("kanban", 1, "tasks"),
            Mig("linux", 1, "idx"),
            Mig("windows", 1, "other"),
        ]);

        var plan = MigrationRunner.Plan(source, "linux", [Component("kanban")]);

        Assert.Equal(["common", "common", "kanban", "linux"], plan.Select(f => f.Namespace));
        Assert.Equal([1, 2, 1, 1], plan.Select(f => f.Version));
    }

    [Fact]
    public void Plan_for_windows_excludes_the_linux_folder()
    {
        var source = new InMemoryMigrationSource([Mig("common", 1), Mig("linux", 1), Mig("windows", 1)]);

        var plan = MigrationRunner.Plan(source, "windows", []);

        Assert.Equal(["common", "windows"], plan.Select(f => f.Namespace));
    }

    [Fact]
    public void Plan_for_common_applies_no_provider_folder()
    {
        var source = new InMemoryMigrationSource([Mig("common", 1), Mig("linux", 1)]);

        var plan = MigrationRunner.Plan(source, MigrationRunner.Common, []);

        Assert.Equal(["common"], plan.Select(f => f.Namespace));
    }

    [Fact]
    public void The_same_version_in_two_namespaces_is_allowed()
    {
        var source = new InMemoryMigrationSource([Mig("common", 1, "a"), Mig("kanban", 1, "b")]);

        var plan = MigrationRunner.Plan(source, "linux", [Component("kanban")]);

        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void Two_files_with_one_version_in_one_namespace_are_refused()
    {
        var source = new InMemoryMigrationSource([Mig("kanban", 1, "a"), Mig("kanban", 1, "b")]);

        var ex = Assert.Throws<MigrationException>(() => MigrationRunner.Plan(source, "linux", [Component("kanban")]));
        Assert.Contains("kanban", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_within_a_namespace_are_planned_in_numeric_order()
    {
        var source = new InMemoryMigrationSource([Mig("kanban", 3), Mig("kanban", 1), Mig("kanban", 2)]);

        var plan = MigrationRunner.Plan(source, "linux", [Component("kanban")]);

        Assert.Equal([1, 2, 3], plan.Select(f => f.Version));
    }

    [Fact]
    public void Embedded_source_finds_the_common_namespace_with_every_shipped_file()
    {
        var source = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);

        Assert.Contains(MigrationNamespaces.Common, source.Namespaces());
        var common = source.Load(MigrationNamespaces.Common);

        Assert.Contains(common, f => f.Version == 1 && f.Name == "core_schema");
        Assert.All(common, f => Assert.Equal(MigrationNamespaces.Common, f.Namespace));
        Assert.Equal(common.Count, common.Select(f => f.Version).Distinct().Count());
    }

    [Fact]
    public void Embedded_source_has_no_namespace_that_breaks_the_name_rule()
    {
        var source = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);

        Assert.All(source.Namespaces(), ns => Assert.True(MigrationNamespaces.IsValid(ns), ns));
    }
}
