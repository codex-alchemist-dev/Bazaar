using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class RoadmapPaneTests
{
    private const string _model = """
        {"phases":[
          {"slug":"alpha","title":"Alpha","gatePassed":false,"objectives":[
            {"path":"roadmap/objectives/a.md","title":"Ship the board","status":"active","owner":"charles","dependsOn":["b"],"linkedTasks":4,"doneTasks":1,"doneShare":0.25},
            {"path":"roadmap/objectives/b.md","title":"Schema","status":"done","owner":null,"dependsOn":[],"linkedTasks":0,"doneTasks":0,"doneShare":null}
          ]}
        ],"unphasedObjectives":[],"dependencyCycle":[]}
        """;

    [Fact]
    public void Reads_phases_and_formats_progress_and_dependencies()
    {
        var pane = new RoadmapPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Roadmap, _model));

        Assert.Equal("RyuseiFuda · Roadmap", pane.Title);
        var phase = Assert.Single(pane.Phases);
        Assert.Equal("gate open", phase.Gate);
        Assert.Equal("Ship the board [active] · 1/4 done (25%) · after b", phase.Objectives[0].Line);
        Assert.Equal("Schema [done] · no linked tasks", phase.Objectives[1].Line);
        Assert.Empty(pane.DependencyCycle);
    }

    [Fact]
    public void Registry_builds_the_roadmap_and_falls_back_on_an_unknown_status()
    {
        var registry = FirstPartyPanes.CreateRegistry();
        Assert.IsType<RoadmapPaneViewModel>(registry.Create(PaneRegistryTests.Descriptor(PaneKinds.Roadmap, _model)));

        var bad = registry.Create(PaneRegistryTests.Descriptor(PaneKinds.Roadmap,
            """{"phases":[{"title":"P","gatePassed":true,"objectives":[{"title":"O","status":"maybe","linkedTasks":0,"doneTasks":0}]}]}"""));
        Assert.IsType<GenericPaneViewModel>(bad);
    }

    [AvaloniaFact]
    public void Roadmap_view_shows_the_cycle_warning_and_objective_lines()
    {
        var pane = new RoadmapPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Roadmap,
            """{"phases":[],"unphasedObjectives":[{"title":"Loose","status":"planned","linkedTasks":0,"doneTasks":0,"dependsOn":["x"]}],"dependencyCycle":["a","b","a"]}"""));

        var view = FirstPartyPanes.CreateRegistry().CreateView(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Dependency cycle: a → b → a", texts);
        Assert.Contains("Loose [planned] · no linked tasks · after x", texts);
    }
}
