using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneHostHeadlessTests
{
    [AvaloniaFact]
    public void Main_window_shows_panes_in_order_and_survives_an_unknown_kind()
    {
        var window = new MainWindow();
        var registry = FirstPartyPanes.CreateRegistry();
        var panes = new[]
        {
            PaneRegistryTests.Descriptor(PaneKinds.Dashboard, "{\"sections\":[{\"title\":\"Awaiting you\",\"items\":[]}]}"),
            PaneRegistryTests.Descriptor("godot.scene-tree", "{\"name\":\"Main\"}"),
        };

        window.ShowPanes(panes, registry);
        window.Show();

        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Awaiting you", texts);
        Assert.Contains("  name: Main", texts);
        Assert.IsType<ScrollViewer>(window.Content);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
    }

    [AvaloniaFact]
    public void Host_builds_one_view_per_descriptor()
    {
        var host = PaneHost.Build(
            [PaneRegistryTests.Descriptor(PaneKinds.Dashboard, "{\"sections\":[]}"), PaneRegistryTests.Descriptor(PaneKinds.Kanban, "{\"tasks\":[]}")],
            FirstPartyPanes.CreateRegistry());

        var stack = Assert.IsType<StackPanel>(host);
        Assert.Equal(2, stack.Children.Count);
    }
}
