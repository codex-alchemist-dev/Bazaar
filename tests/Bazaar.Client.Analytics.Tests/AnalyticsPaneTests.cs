using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Analytics;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Analytics.Tests;

public class AnalyticsPaneTests
{
    private const string _model = """
        {"windowDays":28,
         "company":{"tasksCompleted":9,"tasksPerWeek":2.25,"reviewPassRate":0.75,"revertRate":null},
         "agents":[
           {"agentId":"zed","tasksCompleted":1,"tasksPerWeek":0.25,"reviewPassRate":null,"revertRate":0.5,"hints":["Earlier review could help."]},
           {"agentId":"amy","tasksCompleted":8,"tasksPerWeek":2,"reviewPassRate":1,"revertRate":0,"hints":[]}
         ]}
        """;

    [Fact]
    public void Registers_the_analytics_kind_and_reads_the_model()
    {
        var registry = new PaneRegistry(new PlainViews());
        new AnalyticsPaneModule().Register(registry, new PaneModuleContext());

        Assert.Equal([PaneKinds.Analytics], registry.Kinds);
        var pane = Assert.IsType<AnalyticsPaneViewModel>(registry.Create(Descriptor(_model)));
        Assert.Equal("Tasks per week: 2.25 · Review pass rate: 75% · Revert rate: no data", pane.CompanySummary);
    }

    [Fact]
    public void Missing_rates_show_no_data_not_zero()
    {
        var pane = new AnalyticsPaneViewModel(Descriptor(_model));

        Assert.Equal(AnalyticsPaneViewModel.NoData, pane.Agents[0].ReviewPassRate);
        Assert.Equal("50%", pane.Agents[0].RevertRate);
    }

    [Fact]
    public void Rows_keep_the_server_order_and_are_never_sorted_by_a_figure()
    {
        var pane = new AnalyticsPaneViewModel(Descriptor(_model));

        Assert.Equal(["zed", "amy"], pane.Agents.Select(a => a.AgentId));
    }

    [Fact]
    public void Missing_company_object_throws_pane_model_exception()
    {
        Assert.Throws<PaneModelException>(() => new AnalyticsPaneViewModel(Descriptor("""{"agents":[]}""")));
    }

    [AvaloniaFact]
    public void View_shows_the_company_line_agents_and_hints()
    {
        var pane = new AnalyticsPaneViewModel(Descriptor(_model));

        var texts = AnalyticsPaneView.Create(pane).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToArray();

        Assert.Equal(4, texts.Length);
        Assert.StartsWith("Tasks per week: 2.25", texts[0]);
        Assert.Contains("Could help: Earlier review could help.", texts);
    }

    private static PaneDescriptor Descriptor(string json) =>
        new(PaneKinds.Analytics, "Company analytics", string.Empty, JsonElement.Parse(json));

    private sealed class PlainViews : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Kind };

        public Control WithTitle(string title, Control body) =>
            new StackPanel { Children = { new TextBlock { Text = title }, body } };
    }
}
