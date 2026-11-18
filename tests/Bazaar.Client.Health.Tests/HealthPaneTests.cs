using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Health.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Tests;

public class HealthPaneTests
{
    private const string _model = """
        {"projects":[
          {"projectId":"codex","asOf":"2026-11-17","status":"alert","reasons":[{"kind":"build-failure-streak","message":"3 red builds in a row"}]},
          {"projectId":"ryusei","asOf":"2026-11-17","status":"noData","reasons":[]},
          {"projectId":"kodo","asOf":"2026-11-16","status":"healthy","reasons":[]}
        ]}
        """;

    [Fact]
    public void Reads_projects_and_their_status_labels()
    {
        var pane = new HealthPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Health, _model));

        Assert.Equal("Project health", pane.Title);
        Assert.Equal(3, pane.Projects.Count);
        Assert.Equal("Alert as of 2026-11-17", pane.Projects[0].Line);
        Assert.Equal(new DateOnly(2026, 11, 17), pane.Projects[0].AsOf);
        var reason = Assert.Single(pane.Projects[0].Reasons);
        Assert.Equal("build-failure-streak", reason.Kind);
        Assert.Equal("3 red builds in a row", reason.Message);
    }

    [Fact]
    public void No_data_is_labelled_no_data_and_never_healthy()
    {
        var pane = new HealthPaneViewModel(PaneTestSupport.Descriptor(PaneKinds.Health, _model));

        var noData = pane.Projects[1];
        Assert.Equal("noData", noData.Status);
        Assert.Equal("No data", noData.StatusLabel);
        Assert.Equal("No data as of 2026-11-17", noData.Line);
        Assert.DoesNotContain("Healthy", noData.Line);
        Assert.DoesNotContain("0", noData.StatusLabel);
    }

    [Theory]
    [InlineData("""{"projects":[{"asOf":"2026-11-17","status":"healthy","reasons":[]}]}""")]
    [InlineData("""{"projects":[{"projectId":"p","status":"healthy","reasons":[]}]}""")]
    [InlineData("""{"projects":[{"projectId":"p","asOf":"2026-11-17","reasons":[]}]}""")]
    [InlineData("""{"projects":[{"projectId":"p","asOf":"2026-11-17","status":"healthy"}]}""")]
    public void A_missing_required_field_raises_pane_model_exception(string model)
    {
        var pane = PaneTestSupport.Descriptor(PaneKinds.Health, model);

        Assert.Throws<PaneModelException>(() => new HealthPaneViewModel(pane));
    }

    [Fact]
    public void An_unknown_status_or_a_bad_date_raises_pane_model_exception()
    {
        var unknown = PaneTestSupport.Descriptor(PaneKinds.Health,
            """{"projects":[{"projectId":"p","asOf":"2026-11-17","status":"green","reasons":[]}]}""");
        var badDate = PaneTestSupport.Descriptor(PaneKinds.Health,
            """{"projects":[{"projectId":"p","asOf":"17/11/2026","status":"healthy","reasons":[]}]}""");

        Assert.Throws<PaneModelException>(() => new HealthPaneViewModel(unknown));
        Assert.Throws<PaneModelException>(() => new HealthPaneViewModel(badDate));
    }

    [Fact]
    public void The_registry_falls_back_to_the_generic_view_on_a_missing_field()
    {
        var registry = PaneTestSupport.CreateRegistry();

        var pane = registry.Create(PaneTestSupport.Descriptor(PaneKinds.Health, """{"projects":[{"projectId":"p"}]}"""));

        Assert.IsType<GenericPaneViewModel>(pane);
        Assert.IsType<HealthPaneViewModel>(registry.Create(PaneTestSupport.Descriptor(PaneKinds.Health, _model)));
    }

    [AvaloniaFact]
    public void Health_view_renders_no_data_and_never_a_healthy_label_for_it()
    {
        var registry = PaneTestSupport.CreateRegistry();
        var pane = registry.Create(PaneTestSupport.Descriptor(PaneKinds.Health, _model));

        var view = registry.CreateView(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("ryusei · No data as of 2026-11-17", texts);
        Assert.Contains("codex · Alert as of 2026-11-17", texts);
        Assert.Contains("build-failure-streak: 3 red builds in a row", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith("ryusei · Healthy", StringComparison.Ordinal));
    }
}
