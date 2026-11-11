using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class WorkdayPaneTests
{
    private const string _model = """
        {"owners":[
          {"name":"Charles","tasks":[
            {"title":"Schema","state":"in_progress","dueAt":"2026-11-14T00:00:00+00:00","blockedBy":[]},
            {"title":"Board UI","state":"backlog","dueAt":null,"blockedBy":["Schema"]}
          ]},
          {"name":"Unassigned","tasks":[]}
        ]}
        """;

    [Fact]
    public void Keeps_owner_and_server_order_and_formats_each_task()
    {
        var pane = new WorkdayPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Workday, _model));

        Assert.Equal("RyuseiFuda · Workday", pane.Title);
        Assert.Equal(["Charles", "Unassigned"], pane.Owners.Select(o => o.Name));
        Assert.Equal("Schema [in progress] · due 2026-11-14", pane.Owners[0].Tasks[0].Line);
        Assert.Equal("Board UI [backlog] · blocked by Schema", pane.Owners[0].Tasks[1].Line);
    }

    [Fact]
    public void Registry_builds_the_workday_and_falls_back_on_an_unknown_state()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        Assert.IsType<WorkdayPaneViewModel>(registry.Create(PaneRegistryTests.Descriptor(PaneKinds.Workday, _model)));

        var bad = registry.Create(PaneRegistryTests.Descriptor(PaneKinds.Workday,
            """{"owners":[{"name":"X","tasks":[{"title":"T","state":"stuck"}]}]}"""));
        Assert.IsType<GenericPaneViewModel>(bad);
    }

    [AvaloniaFact]
    public void Workday_view_lists_owner_headings_and_task_lines()
    {
        var pane = new WorkdayPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Workday, _model));

        var view = PaneViews.Create(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Charles (2)", texts);
        Assert.Contains("Board UI [backlog] · blocked by Schema", texts);
        Assert.Contains("Unassigned (0)", texts);
    }
}
