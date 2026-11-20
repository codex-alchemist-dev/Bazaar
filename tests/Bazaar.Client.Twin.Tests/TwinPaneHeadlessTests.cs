using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Twin.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Twin.Tests;

public class TwinPaneHeadlessTests
{
    [AvaloniaFact]
    public void Card_view_labels_stale_facts_and_shows_missing_source_rows()
    {
        var pane = new TwinPaneViewModel(TwinPaneTests.Descriptor(
            """
            {"projects":[{"projectId":"p","title":"Bazaar","facts":[
              {"factId":"f1","kind":"owner","subject":"Pane","value":"charles","confidence":0.5,"status":"stale","trust":"agent","originKind":"agent","sourceRefs":[]}
            ],"pages":[{"page":"overview","sentences":[{"text":"Unsourced line.","sourceRef":null}]}]}]}
            """));

        var view = PaneRegistry.Compose(PaneViews.Instance, TwinPanes.Modules).CreateView(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("STALE", texts);
        Assert.Contains("owner: Pane = charles", texts);
        Assert.Contains("confidence 0.5 · trust agent · origin agent", texts);
        Assert.Equal(2, texts.Count(t => t == "missing source"));
        Assert.Contains("Unsourced line.", texts);
    }
}
