using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class RyuseiSchedulePaneTests
{
    private const string _model = """
        {"triggers":[
          {"id":"b","kind":"interval","state":"active","nextFire":"2026-11-11T09:00:00+00:00","targetNames":["Charles"],"purpose":"Daily check"},
          {"id":"a","kind":"timer","state":"paused","nextFire":null,"targetNames":[],"purpose":"Paused wake"}
        ]}
        """;

    [Fact]
    public void Reads_triggers_in_schedule_order_with_plain_labels()
    {
        var pane = new RyuseiSchedulePaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model));

        Assert.Equal("RyuseiFuda · Schedule", pane.Title);
        Assert.Equal(2, pane.Schedule.Rows.Count);
        Assert.Equal("Repeating interval", pane.Schedule.Rows[0].KindLabel);
        Assert.Equal("Charles", pane.Schedule.Rows[0].TargetsText);
        Assert.Equal("No targets", pane.Schedule.Rows[1].TargetsText);
    }

    [Fact]
    public void Registry_builds_the_schedule_pane_and_falls_back_on_a_bad_timestamp()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        Assert.IsType<RyuseiSchedulePaneViewModel>(registry.Create(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model)));

        var bad = registry.Create(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule,
            """{"triggers":[{"id":"x","kind":"timer","state":"active","nextFire":"soon","targetNames":[],"purpose":"p"}]}"""));
        Assert.IsType<GenericPaneViewModel>(bad);
    }

    [AvaloniaFact]
    public void Schedule_view_shows_the_outcome_labels()
    {
        var pane = new RyuseiSchedulePaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.RyuseiSchedule, _model));

        var view = PaneViews.Create(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Repeating interval · Active", texts);
        Assert.Contains("One-time timer · Paused", texts);
    }
}
