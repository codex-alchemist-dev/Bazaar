using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Health.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Tests;

public class AnalyticsPaneTests
{
    // Coverage has points on 1, 2 and 6 Nov; 3 to 5 Nov have no point; the 4 Nov point exists but has no value.
    private const string _model = """
        {"projectId":"codex","windowDays":30,
         "series":[{"metric":"coverage","unit":"percent","points":[
           {"day":"2026-11-01","value":80},
           {"day":"2026-11-02","value":82.5},
           {"day":"2026-11-04","value":null},
           {"day":"2026-11-06","value":84}
         ]}],
         "moraleSignals":[{"signal":"blocked_share","value":0.1}],
         "agentView":{"visibility":"owner+self","framing":"What would help the company",
           "agents":[
             {"agentId":"a2","displayName":"Zed","metrics":[{"metric":"reopened_tasks","value":9}]},
             {"agentId":"a1","displayName":"Ada","metrics":[{"metric":"reopened_tasks","value":1}]}
           ]}}
        """;

    [Fact]
    public void Reads_the_series_points_and_the_morale_signals()
    {
        var pane = new AnalyticsPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Analytics, _model));

        Assert.Equal("Analytics", pane.Title);
        Assert.Equal("codex", pane.ProjectId);
        Assert.Equal(30, pane.WindowDays);
        var series = Assert.Single(pane.Series);
        Assert.Equal("coverage", series.Metric);
        Assert.Equal(4, series.Points.Count);
        Assert.Equal(82.5, series.Points[1].Value);
        var signal = Assert.Single(pane.MoraleSignals);
        Assert.Equal("blocked_share", signal.Signal);
        Assert.Equal("0.1", signal.Value);
    }

    [Fact]
    public void A_missing_day_and_a_null_value_are_gaps_and_never_zero()
    {
        var pane = new AnalyticsPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Analytics, _model));

        var series = Assert.Single(pane.Series);
        Assert.Equal(
            [new DayRange(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 5))],
            series.Gaps);
        Assert.DoesNotContain(series.Points, p => p.Value == 0);
        Assert.Null(series.Points[2].Value);
    }

    [Fact]
    public void A_single_missing_day_between_points_is_one_gap()
    {
        var model = """
            {"projectId":"p","windowDays":7,"series":[{"metric":"m","unit":"u","points":[
              {"day":"2026-11-01","value":1},{"day":"2026-11-03","value":2}]}],"moraleSignals":[]}
            """;

        var pane = new AnalyticsPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Analytics, model));

        Assert.Equal([new DayRange(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2))], Assert.Single(pane.Series).Gaps);
    }

    [Fact]
    public void Agents_are_ordered_by_display_name_and_never_by_value()
    {
        var pane = new AnalyticsPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Analytics, _model));

        var view = pane.AgentView!;
        Assert.Equal("owner+self", view.Visibility);
        Assert.Equal("What would help the company", view.Framing);
        Assert.Equal(["Ada", "Zed"], view.Agents.Select(a => a.DisplayName));
    }

    [Fact]
    public void Agent_view_is_optional_and_its_visibility_is_checked()
    {
        var without = new AnalyticsPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Analytics,
            """{"projectId":"p","windowDays":7,"series":[],"moraleSignals":[]}"""));
        Assert.Null(without.AgentView);

        var badVisibility = PaneTestSupport.Descriptor(PaneKinds.Analytics,
            """{"projectId":"p","windowDays":7,"series":[],"moraleSignals":[],"agentView":{"visibility":"everyone","framing":"f","agents":[]}}""");
        Assert.Throws<PaneModelException>(() => new AnalyticsPaneViewModel(badVisibility));
    }

    [Theory]
    [InlineData("""{"windowDays":7,"series":[],"moraleSignals":[]}""")]
    [InlineData("""{"projectId":"p","series":[],"moraleSignals":[]}""")]
    [InlineData("""{"projectId":"p","windowDays":7,"moraleSignals":[]}""")]
    [InlineData("""{"projectId":"p","windowDays":7,"series":[{"unit":"u","points":[]}],"moraleSignals":[]}""")]
    [InlineData("""{"projectId":"p","windowDays":7,"series":[{"metric":"m","unit":"u","points":[{"day":"2026-11-01"}]}],"moraleSignals":[]}""")]
    public void A_missing_required_field_raises_pane_model_exception(string model)
    {
        var pane = PaneTestSupport.Descriptor(PaneKinds.Analytics, model);

        Assert.Throws<PaneModelException>(() => new AnalyticsPaneViewModel(pane));
    }

    [Fact]
    public void The_registry_falls_back_to_the_generic_view_on_a_missing_field()
    {
        var registry = PaneTestSupport.CreateRegistry();

        Assert.IsType<GenericPaneViewModel>(registry.Create(PaneTestSupport.Descriptor(PaneKinds.Analytics, """{"projectId":"p"}""")));
        Assert.IsType<AnalyticsPaneViewModel>(registry.Create(PaneTestSupport.Descriptor(PaneKinds.Analytics, _model)));
    }

    [AvaloniaFact]
    public void Analytics_view_shows_gaps_and_the_agent_view_in_name_order_without_rank()
    {
        var registry = PaneTestSupport.CreateRegistry();
        var pane = registry.Create(PaneTestSupport.Descriptor(PaneKinds.Analytics, _model));

        var view = registry.CreateView(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("no data 2026-11-03 to 2026-11-05", texts);
        Assert.Contains("What would help the company", texts);
        Assert.Contains("2026-11-01: 80", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.Contains("rank", StringComparison.OrdinalIgnoreCase));
        Assert.True(texts.IndexOf("Ada") < texts.IndexOf("Zed"));
    }
}
