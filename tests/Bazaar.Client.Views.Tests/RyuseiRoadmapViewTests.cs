using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Contracts.Panes;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

/// <summary>The RyuseiFuda roadmap pane as a view document (doc 41c): phases, unphased objectives and a dependency cycle.</summary>
public class RyuseiRoadmapViewTests
{
    private const string _model = """
        { "phases": [ { "slug": "p1", "title": "Foundations", "gatePassed": true, "objectives": [
            { "path": "a", "title": "Ship core", "status": "done", "owner": "x", "dependsOn": [], "linkedTasks": 2, "doneTasks": 2, "doneShare": 1 } ] } ],
          "unphasedObjectives": [
            { "path": "b", "title": "Explore", "status": "planned", "owner": "y", "dependsOn": ["a"], "linkedTasks": 0, "doneTasks": 0 } ],
          "dependencyCycle": [ "a", "b", "a" ] }
        """;

    private static RoadmapPaneViewModel Pane(string model)
    {
        using JsonDocument json = JsonDocument.Parse(model);
        return new RoadmapPaneViewModel(new PaneDescriptor(PaneKinds.Roadmap, "Roadmap", "ryusei.roadmap", json.RootElement.Clone()));
    }

    private static ViewDocument Document() => ViewDocumentReader.Read(RyuseiRoadmapViewData.ReadViewJson());

    private static string[] VisibleTexts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? string.Empty).ToArray();

    [Fact]
    public void The_document_validates_clean()
    {
        Assert.Empty(ViewValidator.Validate(Document()));
    }

    [AvaloniaFact]
    public void Phases_objectives_and_the_cycle_are_shown()
    {
        Control root = new Harness().Build(Document(), data: RyuseiRoadmapViewData.From(Pane(_model)));

        string[] texts = VisibleTexts(root);

        Assert.Contains("Foundations · gate passed", texts);
        Assert.Contains("Ship core [done] · 2/2 done (100%)", texts);
        Assert.Contains("Unphased", texts);
        Assert.Contains("Explore [planned] · no linked tasks · after a", texts);
        Assert.Contains("Dependency cycle: a → b → a", texts);
        Assert.DoesNotContain("No objectives yet.", texts);
    }

    [AvaloniaFact]
    public void An_empty_roadmap_says_so()
    {
        Control root = new Harness().Build(Document(), data: RyuseiRoadmapViewData.From(Pane("""{ "phases": [] }""")));

        Assert.Contains("No objectives yet.", VisibleTexts(root));
    }
}
