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
        var registry = TestPanes.CreateRegistry();
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
            TestPanes.CreateRegistry());

        var stack = Assert.IsType<StackPanel>(host);
        Assert.Equal(2, stack.Children.Count);
    }

    [AvaloniaFact]
    public void Host_renders_an_unknown_kind_as_a_placeholder_under_its_own_title()
    {
        var host = PaneHost.Build(
            [PaneRegistryTests.Descriptor("godot.scene-tree", "{\"name\":\"Main\"}") with { Title = "Scene tree" }],
            TestPanes.CreateRegistry());

        var texts = host.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(["Scene tree", "{", "  name: Main"], texts.Take(3));
    }

    [AvaloniaFact]
    public void Host_keeps_descriptor_order_for_known_and_unknown_kinds()
    {
        var host = PaneHost.Build(
            [
                PaneRegistryTests.Descriptor(PaneKinds.Dashboard, "{\"sections\":[]}") with { Title = "First" },
                PaneRegistryTests.Descriptor("godot.scene-tree", "{}") with { Title = "Second" },
                PaneRegistryTests.Descriptor(PaneKinds.Kanban, "{\"cards\":[]}") with { Title = "Third" },
            ],
            TestPanes.CreateRegistry());

        var stack = Assert.IsType<StackPanel>(host);
        var titles = stack.Children
            .Select(child => Assert.IsType<StackPanel>(child).Children.OfType<TextBlock>().First().Text)
            .ToList();
        Assert.Equal(["First", "Second", "Third"], titles);
    }

    [Fact]
    public void Host_with_no_descriptors_builds_an_empty_stack()
    {
        var host = PaneHost.Build([], TestPanes.CreateRegistry());

        var stack = Assert.IsType<StackPanel>(host);
        Assert.Empty(stack.Children);
    }

    [Fact]
    public void Host_rejects_null_arguments()
    {
        var registry = TestPanes.CreateRegistry();

        Assert.Throws<ArgumentNullException>(() => PaneHost.Build(null!, registry));
        Assert.Throws<ArgumentNullException>(() => PaneHost.Build([], null!));
    }

    [AvaloniaFact]
    public void Showing_panes_again_replaces_the_earlier_panes()
    {
        var window = new MainWindow();
        var registry = TestPanes.CreateRegistry();

        window.ShowPanes([PaneRegistryTests.Descriptor("godot.scene-tree", "{}") with { Title = "First pane" }], registry);
        window.ShowPanes([PaneRegistryTests.Descriptor("godot.scene-tree", "{}") with { Title = "Second pane" }], registry);
        window.Show();

        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Second pane", texts);
        Assert.DoesNotContain("First pane", texts);
    }

    [AvaloniaFact]
    public void Showing_an_empty_pane_list_clears_the_visible_panes()
    {
        var window = new MainWindow();
        var registry = TestPanes.CreateRegistry();

        window.ShowPanes([PaneRegistryTests.Descriptor("godot.scene-tree", "{}") with { Title = "Gone pane" }], registry);
        window.ShowPanes([], registry);
        window.Show();

        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain("Gone pane", texts);
        Assert.IsType<ScrollViewer>(window.Content);
    }
}
