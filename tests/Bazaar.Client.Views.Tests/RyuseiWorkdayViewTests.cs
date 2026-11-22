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

/// <summary>The RyuseiFuda workday pane as a view document (doc 41c): owner headings and task lines in the server's order.</summary>
public class RyuseiWorkdayViewTests
{
    private static WorkdayPaneViewModel Pane(string model)
    {
        using JsonDocument json = JsonDocument.Parse(model);
        return new WorkdayPaneViewModel(new PaneDescriptor(PaneKinds.Workday, "Workday", "ryusei.workday", json.RootElement.Clone()));
    }

    private static ViewDocument Document() => ViewDocumentReader.Read(RyuseiWorkdayViewData.ReadViewJson());

    private static string[] VisibleTexts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? string.Empty).ToArray();

    [Fact]
    public void The_document_validates_clean()
    {
        Assert.Empty(ViewValidator.Validate(Document()));
    }

    [AvaloniaFact]
    public void Owners_and_their_tasks_are_listed_in_order()
    {
        const string model = """
            { "owners": [
              { "name": "Charles", "tasks": [
                { "title": "Fix build", "state": "in_progress", "dueAt": "2026-11-20T00:00:00Z", "blockedBy": [] },
                { "title": "Write docs", "state": "ready", "blockedBy": ["Fix build"] } ] },
              { "name": "Ada", "tasks": [] } ] }
            """;

        Control root = new Harness().Build(Document(), data: RyuseiWorkdayViewData.From(Pane(model)));

        string[] texts = VisibleTexts(root);
        Assert.Equal(
            ["Charles (2)", "Fix build [in progress] · due 2026-11-20", "Write docs [ready] · blocked by Fix build", "Ada (0)"],
            texts);
    }

    [AvaloniaFact]
    public void An_empty_board_says_so()
    {
        Control root = new Harness().Build(Document(), data: RyuseiWorkdayViewData.From(Pane("""{ "owners": [] }""")));

        Assert.Equal(["No tasks on the board."], VisibleTexts(root));
    }
}
