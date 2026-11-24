using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.CompanyMemory.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.CompanyMemory.Tests;

/// <summary>Headless render checks for the decision chain pane. They run without a display server.</summary>
public class CompanyMemoryPaneHeadlessTests
{
    private const string _model = """
        {
          "decisions": [
            { "path": "decisions/0001-use-polling.md", "title": "Use polling", "status": "superseded", "decidedIn": "gc-msg-1" },
            { "path": "decisions/0002-use-notify.md", "title": "Use NOTIFY", "status": "accepted", "decidedIn": null }
          ],
          "links": [
            { "fromDoc": "decisions/0002-use-notify.md", "toDoc": "decisions/0001-use-polling.md",
              "relation": "supersedes", "status": "accepted", "fromMissing": false, "toMissing": false },
            { "fromDoc": "decisions/0009-gone.md", "toDoc": "decisions/0001-use-polling.md",
              "relation": "supersedes", "status": null, "fromMissing": true, "toMissing": false }
          ]
        }
        """;

    [AvaloniaFact]
    public void Decision_chain_renders_to_a_frame()
    {
        var pane = new CompanyMemoryPaneViewModel(new PaneDescriptor(PaneKinds.CompanyMemory, "Title", string.Empty, JsonElement.Parse(_model)));
        var window = new Window { Content = Views(pane), Width = 800, Height = 600 };

        window.Show();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public void Decision_chain_shows_superseded_marks_decided_in_and_missing_source()
    {
        var pane = new CompanyMemoryPaneViewModel(new PaneDescriptor(PaneKinds.CompanyMemory, "Title", string.Empty, JsonElement.Parse(_model)));

        var view = Views(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Use polling [superseded] · replaced by a newer decision", texts);
        Assert.Contains("decisions/0001-use-polling.md · decided in gc-msg-1", texts);
        Assert.Contains("decisions/0002-use-notify.md · no decision message recorded", texts);
        Assert.Contains("decisions/0002-use-notify.md supersedes decisions/0001-use-polling.md", texts);
        Assert.Contains("decisions/0009-gone.md supersedes decisions/0001-use-polling.md", texts);
        Assert.Contains("missing source: decisions/0009-gone.md", texts);
    }

    [AvaloniaFact]
    public void Empty_chain_shows_a_no_decisions_note()
    {
        var pane = new CompanyMemoryPaneViewModel(new PaneDescriptor(PaneKinds.CompanyMemory, "Title", string.Empty, JsonElement.Parse("""{"decisions":[],"links":[]}""")));

        var texts = Views(pane).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("No decisions recorded.", texts);
    }

    private static Control Views(CompanyMemoryPaneViewModel pane) =>
        PaneRegistry.Compose(PaneViews.Instance, CompanyMemoryPanes.Modules).CreateView(pane);
}
