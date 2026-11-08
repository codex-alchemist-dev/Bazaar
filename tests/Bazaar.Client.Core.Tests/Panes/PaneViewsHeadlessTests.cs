using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneViewsHeadlessTests
{
    [AvaloniaFact]
    public void Each_first_party_pane_renders_to_a_frame()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        var panes = new[]
        {
            registry.Create(PaneRegistryTests.Descriptor(Bazaar.Contracts.Panes.PaneKinds.Kanban, """
                {"tasks":[{"id":"t1","title":"Schema","state":"Ready","blocked":true}]}
                """)),
            registry.Create(PaneRegistryTests.Descriptor(Bazaar.Contracts.Panes.PaneKinds.Dashboard, """
                {"sections":[{"title":"Awaiting you","items":[{"label":"Approvals","value":2}]}]}
                """)),
            registry.Create(PaneRegistryTests.Descriptor("godot.scene-tree", "{\"name\":\"Main\"}")),
        };

        foreach (var pane in panes)
        {
            var window = new Window { Content = PaneViews.Create(pane), Width = 800, Height = 600 };
            window.Show();

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Kanban_view_shows_column_counts_and_blocked_marking()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor(
            "bazaar.kanban",
            "{\"cards\":[{\"id\":\"0196a6f0-0000-7000-8000-000000000001\",\"title\":\"Schema\",\"state\":\"ready\",\"unfinishedBlockerIds\":[\"0196a6f0-0000-7000-8000-000000000009\"],\"blockerTitles\":[\"Other\"]}]}"));

        var view = PaneViews.Create(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Ready (1)", texts);
        Assert.Contains("Schema [Blocked]", texts);
    }
}
