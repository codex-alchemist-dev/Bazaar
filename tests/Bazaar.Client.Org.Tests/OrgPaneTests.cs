using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Org.Panes;
using Bazaar.Contracts.Panes;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Org.Tests.TestAppBuilder))]

namespace Bazaar.Client.Org.Tests;

/// <summary>Builds the Avalonia application for headless tests: no display server is needed.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class OrgPaneTests
{
    private const string _kind = "bazaar.org";

    private const string _model = """
        {"projects":[{"id":"p1","name":"Bazaar"}],
         "agents":[{"id":"a","name":"Ada"},{"id":"b","name":"Ben"},{"id":"c","name":"Cy"}],
         "edges":[
          {"id":"e1","kind":"reports_to","fromAgentId":"b","toAgentId":"a","projectId":null,"startsAt":"2026-01-01T00:00:00Z","endsAt":null},
          {"id":"e2","kind":"reports_to","fromAgentId":"c","toAgentId":"b","projectId":null,"startsAt":"2026-01-01T00:00:00Z","endsAt":null},
          {"id":"e3","kind":"lent_to","fromAgentId":"b","toAgentId":"a","projectId":"p1","startsAt":"2026-02-01T00:00:00Z","endsAt":"2026-03-01T00:00:00Z"},
          {"id":"e4","kind":"lent_to","fromAgentId":"c","toAgentId":"a","projectId":"p1","startsAt":"2026-02-01T00:00:00Z","endsAt":"2099-03-01T00:00:00Z"}
         ]}
        """;

    private static readonly TimeProvider _now = new FixedTime(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static PaneDescriptor Descriptor(string json) =>
        new(_kind, "Org", "org", JsonElement.Parse(json));

    [Fact]
    public void Company_lens_draws_reports_to_as_a_tree()
    {
        var pane = new OrgPaneViewModel(Descriptor(_model), _now);

        Assert.Equal(["Ada", "Ben", "Cy"], pane.Tree.Select(r => r.Label));
        Assert.Equal([0, 1, 2], pane.Tree.Select(r => r.Depth));
        Assert.Empty(pane.Loans);
        Assert.Equal(["Company", "Bazaar"], pane.Lenses.Select(l => l.Name));
    }

    [Fact]
    public void Project_lens_shows_loans_and_dims_ended_ones()
    {
        var pane = new OrgPaneViewModel(Descriptor(_model), _now);
        pane.SelectLens("p1");

        Assert.Empty(pane.Tree);
        Assert.Equal(2, pane.Loans.Count);
        Assert.True(pane.Loans[0].IsEnded);
        Assert.Equal("Ben lent to Ada · 2026-02-01 to 2026-03-01 · ended", pane.Loans[0].Line);
        Assert.False(pane.Loans[1].IsEnded);
    }

    [Fact]
    public void Cycles_and_missing_agents_terminate_and_mark_repeats()
    {
        const string json = """
            {"projects":[],"agents":[{"id":"a","name":"Ada"},{"id":"b","name":"Ben"}],
             "edges":[
              {"id":"e1","kind":"reports_to","fromAgentId":"a","toAgentId":"b","projectId":null,"startsAt":"2026-01-01T00:00:00Z","endsAt":null},
              {"id":"e2","kind":"reports_to","fromAgentId":"b","toAgentId":"a","projectId":null,"startsAt":"2026-01-01T00:00:00Z","endsAt":null},
              {"id":"e3","kind":"reports_to","fromAgentId":"ghost","toAgentId":"a","projectId":null,"startsAt":"2026-01-01T00:00:00Z","endsAt":null}
             ]}
            """;

        var pane = new OrgPaneViewModel(Descriptor(json), _now);

        Assert.Equal(pane.Tree.Where(r => !r.IsRepeat).Select(r => r.AgentId).Distinct().Count(), pane.Tree.Count(r => !r.IsRepeat));
        Assert.Contains(pane.Tree, r => r.IsRepeat);
        Assert.Contains(pane.Tree, r => r.Label == "unknown agent (ghost)");
    }

    [Theory]
    [InlineData("""{"projects":[],"agents":[]}""")]
    [InlineData("""{"projects":[],"agents":[],"edges":[{"id":"e","kind":"covers","fromAgentId":"a","toAgentId":"b","projectId":null,"startsAt":"2026-01-01T00:00:00Z","endsAt":null}]}""")]
    [InlineData("""{"projects":[],"agents":[],"edges":[{"id":"e","kind":"lent_to","fromAgentId":"a","toAgentId":"b","projectId":null,"startsAt":"not a date","endsAt":null}]}""")]
    [InlineData("""[]""")]
    public void Malformed_models_raise_pane_model_exception(string json)
    {
        Assert.Throws<PaneModelException>(() => new OrgPaneViewModel(Descriptor(json), _now));
    }

    [Fact]
    public void Registry_builds_the_pane_and_falls_back_on_a_malformed_model()
    {
        var registry = PaneRegistry.Compose(OrgPanes.Modules);

        Assert.IsType<OrgPaneViewModel>(registry.Create(Descriptor(_model)));
        Assert.IsNotType<OrgPaneViewModel>(registry.Create(Descriptor("""{"projects":1}""")));
    }

    [AvaloniaFact]
    public void View_names_its_controls_states_no_authority_and_switches_lens()
    {
        var registry = PaneRegistry.Compose(OrgPanes.Modules);
        var pane = Assert.IsType<OrgPaneViewModel>(registry.Create(Descriptor(_model)));
        var view = registry.CreateView(pane);
        var window = new Window { Content = view };
        window.Show();

        var combo = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
        Assert.Equal("Org lens", AutomationProperties.GetName(combo));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == OrgPaneViewModel.NoAuthorityNotice);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ben");

        combo.SelectedIndex = 1;

        Assert.Equal("p1", pane.SelectedLens.ProjectId);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ben");
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text!.EndsWith("ended", StringComparison.Ordinal));
        window.Close();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
