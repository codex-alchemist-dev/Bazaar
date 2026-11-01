using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class DashboardPaneViewModelTests
{
    [Fact]
    public void Reads_sections_items_and_numeric_values()
    {
        var pane = new DashboardPaneViewModel(PaneRegistryTests.Descriptor("bazaar.dashboard", """
            {"sections":[
              {"title":"While you were away","items":[
                {"label":"Unread GCs","value":3},
                {"label":"Mentions","value":"1"}
              ]},
              {"title":"Awaiting you","items":[{"label":"Approvals","value":true}]}
            ]}
            """));

        Assert.Equal(2, pane.Sections.Count);
        Assert.Equal("While you were away", pane.Sections[0].Title);
        Assert.Equal(new DashboardItem("Unread GCs", "3"), pane.Sections[0].Items[0]);
        Assert.Equal(new DashboardItem("Mentions", "1"), pane.Sections[0].Items[1]);
        Assert.Equal(new DashboardItem("Approvals", "yes"), pane.Sections[1].Items[0]);
    }

    [Fact]
    public void Empty_sections_is_valid()
    {
        var pane = new DashboardPaneViewModel(PaneRegistryTests.Descriptor("bazaar.dashboard", "{\"sections\":[]}"));

        Assert.Empty(pane.Sections);
    }

    [Fact]
    public void Missing_items_throws_pane_model_exception()
    {
        Assert.Throws<PaneModelException>(() =>
            new DashboardPaneViewModel(PaneRegistryTests.Descriptor("bazaar.dashboard", "{\"sections\":[{\"title\":\"T\"}]}")));
    }

    [Fact]
    public void Object_value_is_rejected()
    {
        Assert.Throws<PaneModelException>(() =>
            new DashboardPaneViewModel(PaneRegistryTests.Descriptor(
                "bazaar.dashboard",
                "{\"sections\":[{\"title\":\"T\",\"items\":[{\"label\":\"L\",\"value\":{}}]}]}")));
    }
}
