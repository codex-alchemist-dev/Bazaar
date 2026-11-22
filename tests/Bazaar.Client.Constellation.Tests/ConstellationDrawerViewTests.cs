using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Bazaar.Client.Constellation.Tests;

public class ConstellationDrawerViewTests
{
    private static ConstellationDrawerHost Host(ConstellationDrawerViewModel model, out Window window)
    {
        var host = new ConstellationDrawerHost(new TextBlock { Text = "phone shell" }, model);
        window = new Window { Content = host, Width = 400, Height = 800 };
        window.Show();
        return host;
    }

    private static ConstellationDrawerView ViewOf(ConstellationDrawerHost host) =>
        host.GetLogicalDescendants().OfType<ConstellationDrawerView>().Single();

    private static Button Handle(ConstellationDrawerHost host) =>
        host.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("drawer-handle"));

    private static Button[] NodeButtons(ConstellationDrawerHost host) =>
        host.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("drawer-node")).ToArray();

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void The_drawer_is_hidden_when_folded()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(null));
        var host = Host(model, out var window);

        Assert.False(host.IsDrawerAvailable);
        Assert.False(Handle(host).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Unfolded_shows_a_collapsed_handle_that_toggles_the_body()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(null));
        var host = Host(model, out var window);
        host.IsDrawerAvailable = true;
        var view = ViewOf(host);

        Assert.True(Handle(host).IsEffectivelyVisible);
        Assert.False(view.IsEffectivelyVisible);

        Click(Handle(host));

        Assert.True(host.IsExpanded);
        Assert.True(view.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_list_shows_one_button_per_listed_node_and_the_count()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha", "beta"])));
        var host = Host(model, out var window);
        host.IsDrawerAvailable = true;

        model.Refresh(DrawerTestData.Box, DrawerTestData.Graph).GetAwaiter().GetResult();

        var buttons = NodeButtons(host);
        Assert.Equal(["alpha", "beta"], buttons.Select(b => (string)b.Tag!));
        Assert.Equal("2 nodes in this area.", ViewOf(host).StatusText);
    }

    [AvaloniaFact]
    public void The_status_says_why_no_nodes_are_listed()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(null));
        var host = Host(model, out var window);

        Assert.Equal("Waiting for the graph from the server.", ViewOf(host).StatusText);
        Assert.Empty(NodeButtons(host));

        model.Refresh(DrawerTestData.Box, new DrawerGraphState(GraphVersion: 7, ClustersOnly: true)).GetAwaiter().GetResult();

        Assert.Equal("The graph is above the node cap. Nodes are not listed here.", ViewOf(host).StatusText);
    }

    [AvaloniaFact]
    public void Clicking_a_node_opens_its_detail_and_close_hides_it()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha", "beta"])));
        var host = Host(model, out var window);
        host.IsDrawerAvailable = true;
        model.Refresh(DrawerTestData.Box, DrawerTestData.Graph).GetAwaiter().GetResult();

        Click(NodeButtons(host)[1]);

        Assert.Equal("beta", model.OpenedNode?.Id);
        var detail = host.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("drawer-detail"));
        Assert.True(detail.IsEffectivelyVisible);
        Assert.Contains("Node beta", detail.Text);

        Click(host.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("drawer-close")));

        Assert.Null(model.OpenedNode);
        Assert.False(detail.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_drawer_has_no_editing_controls()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha"])));
        var host = Host(model, out var window);
        host.IsDrawerAvailable = true;
        model.Refresh(DrawerTestData.Box, DrawerTestData.Graph).GetAwaiter().GetResult();

        Assert.DoesNotContain(host.GetLogicalDescendants(), c => c is TextBlock { } t && t.Classes.Contains("editable"));
    }
}
