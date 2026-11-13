using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class WindowPanesTests
{
    [Fact]
    public void Window_panes_are_kanban_then_constellation_with_object_models()
    {
        var panes = FirstPartyPanes.WindowPanes();

        Assert.Equal([PaneKinds.Kanban, PaneKinds.Constellation], panes.Select(pane => pane.Kind).ToArray());
        Assert.All(panes, pane => Assert.Equal(JsonValueKind.Object, pane.Model.ValueKind));
    }

    [Fact]
    public void Kanban_pane_title_is_the_ryusei_fuda_board()
    {
        var kanban = FirstPartyPanes.WindowPanes().Single(pane => pane.Kind == PaneKinds.Kanban);

        Assert.Equal("RyuseiFuda · Board", kanban.Title);
    }

    [Fact]
    public void Window_panes_build_through_the_first_party_registry_without_fallback()
    {
        var reports = new List<string>();
        var registry = FirstPartyPanes.CreateRegistry(report: reports.Add);

        foreach (var pane in FirstPartyPanes.WindowPanes())
        {
            registry.Create(pane);
        }

        Assert.Empty(reports);
    }
}
