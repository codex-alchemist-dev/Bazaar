using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneRegistryTests
{
    [Fact]
    public void Known_kind_uses_its_factory()
    {
        var registry = new PaneRegistry();
        registry.Register("test.kind", d => new GenericPaneViewModel(d));

        var pane = registry.Create(Descriptor("test.kind"));

        Assert.IsType<GenericPaneViewModel>(pane);
        Assert.True(registry.Contains("test.kind"));
    }

    [Fact]
    public void Unknown_kind_falls_back_to_generic_and_reports_once()
    {
        var reports = new List<string>();
        var registry = new PaneRegistry(reports.Add);

        var first = registry.Create(Descriptor("godot.scene-tree", "{\"nodes\":1}"));
        var second = registry.Create(Descriptor("godot.scene-tree", "{\"nodes\":2}"));

        var generic = Assert.IsType<GenericPaneViewModel>(first);
        Assert.Equal("godot.scene-tree", generic.Kind);
        Assert.IsType<GenericPaneViewModel>(second);
        Assert.Single(reports);
        Assert.Contains("godot.scene-tree", reports[0]);
    }

    [Fact]
    public void Malformed_model_falls_back_to_generic_with_reason()
    {
        var reports = new List<string>();
        var registry = TestPanes.CreateRegistry(reports.Add);

        var pane = registry.Create(Descriptor(PaneKinds.Kanban, "{\"cards\":\"nope\"}"));

        Assert.IsType<GenericPaneViewModel>(pane);
        Assert.Single(reports);
        Assert.Contains("cards", reports[0]);
    }

    [Fact]
    public void Duplicate_registration_throws()
    {
        var registry = new PaneRegistry();
        registry.Register("test.kind", d => new GenericPaneViewModel(d));

        Assert.Throws<InvalidOperationException>(() => registry.Register("test.kind", d => new GenericPaneViewModel(d)));
    }

    [Fact]
    public void Kinds_list_registered_kinds_in_registration_order()
    {
        var registry = new PaneRegistry();
        registry.Register("test.b", d => new GenericPaneViewModel(d));
        registry.Register("test.a", d => new GenericPaneViewModel(d));
        registry.Register("test.c", d => new GenericPaneViewModel(d));

        Assert.Equal(["test.b", "test.a", "test.c"], registry.Kinds);
    }

    [Fact]
    public void Blank_kind_is_rejected_at_registration()
    {
        var registry = new PaneRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.Register(string.Empty, d => new GenericPaneViewModel(d)));
        Assert.ThrowsAny<ArgumentException>(() => registry.Register("   ", d => new GenericPaneViewModel(d)));
        Assert.Empty(registry.Kinds);
    }

    [Fact]
    public void Null_factory_is_rejected_at_registration()
    {
        var registry = new PaneRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register("test.kind", null!));
        Assert.False(registry.Contains("test.kind"));
    }

    [Fact]
    public void Duplicate_registration_keeps_the_first_factory_and_kind()
    {
        var registry = new PaneRegistry();
        registry.Register("test.kind", d => new GenericPaneViewModel(d with { Title = "first" }));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register("test.kind", d => new GenericPaneViewModel(d with { Title = "second" })));

        Assert.Equal(["test.kind"], registry.Kinds);
        Assert.Equal("first", registry.Create(Descriptor("test.kind")).Title);
    }

    [Fact]
    public void Unknown_kind_falls_back_keeping_the_descriptor_kind_and_title()
    {
        var registry = new PaneRegistry();

        var pane = registry.Create(Descriptor("godot.scene-tree") with { Title = "Scene" });

        var generic = Assert.IsType<GenericPaneViewModel>(pane);
        Assert.Equal("godot.scene-tree", generic.Kind);
        Assert.Equal("Scene", generic.Title);
        Assert.False(registry.Contains("godot.scene-tree"));
        Assert.Empty(registry.Kinds);
    }

    [Fact]
    public void Unknown_kind_without_a_report_callback_does_not_throw()
    {
        var registry = new PaneRegistry();

        var pane = registry.Create(Descriptor("godot.scene-tree"));

        Assert.IsType<GenericPaneViewModel>(pane);
    }

    [Fact]
    public void Bad_model_is_reported_once_per_kind_across_descriptors()
    {
        var reports = new List<string>();
        var registry = TestPanes.CreateRegistry(reports.Add);

        registry.Create(Descriptor(PaneKinds.Kanban, "{\"cards\":\"nope\"}"));
        registry.Create(Descriptor(PaneKinds.Kanban, "{\"cards\":42}"));

        Assert.Single(reports);
        Assert.Contains(PaneKinds.Kanban, reports[0]);
    }

    [Fact]
    public void Create_rejects_a_null_descriptor()
    {
        var registry = new PaneRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Create(null!));
    }

    [Fact]
    public void Factory_errors_other_than_a_bad_model_propagate_and_are_not_reported()
    {
        var reports = new List<string>();
        var registry = new PaneRegistry(reports.Add);
        registry.Register("test.boom", _ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => registry.Create(Descriptor("test.boom")));
        Assert.Empty(reports);
    }

    [Fact]
    public void First_party_registry_covers_kanban_and_dashboard()
    {
        var registry = TestPanes.CreateRegistry();

        Assert.True(registry.Contains(PaneKinds.Kanban));
        Assert.True(registry.Contains(PaneKinds.Dashboard));
        Assert.IsType<KanbanPaneViewModel>(registry.Create(Descriptor(PaneKinds.Kanban, "{\"cards\":[]}")));
        Assert.IsType<DashboardPaneViewModel>(registry.Create(Descriptor(PaneKinds.Dashboard, "{\"sections\":[]}")));
    }

    internal static PaneDescriptor Descriptor(string kind, string json = "{}") =>
        new(kind, "Title", string.Empty, JsonElement.Parse(json));
}
