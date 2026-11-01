using System.Text.Json;
using Bazaar.Client.Core.Panes;
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
        var registry = FirstPartyPanes.CreateRegistry(reports.Add);

        var pane = registry.Create(Descriptor(PaneKinds.Kanban, "{\"tasks\":\"nope\"}"));

        Assert.IsType<GenericPaneViewModel>(pane);
        Assert.Single(reports);
        Assert.Contains("tasks", reports[0]);
    }

    [Fact]
    public void Duplicate_registration_throws()
    {
        var registry = new PaneRegistry();
        registry.Register("test.kind", d => new GenericPaneViewModel(d));

        Assert.Throws<InvalidOperationException>(() => registry.Register("test.kind", d => new GenericPaneViewModel(d)));
    }

    [Fact]
    public void First_party_registry_covers_kanban_and_dashboard()
    {
        var registry = FirstPartyPanes.CreateRegistry();

        Assert.True(registry.Contains(PaneKinds.Kanban));
        Assert.True(registry.Contains(PaneKinds.Dashboard));
        Assert.IsType<KanbanPaneViewModel>(registry.Create(Descriptor(PaneKinds.Kanban, "{\"tasks\":[]}")));
        Assert.IsType<DashboardPaneViewModel>(registry.Create(Descriptor(PaneKinds.Dashboard, "{\"sections\":[]}")));
    }

    internal static PaneDescriptor Descriptor(string kind, string json = "{}") =>
        new(kind, "Title", string.Empty, JsonElement.Parse(json));
}
