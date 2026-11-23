using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Layout;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.LayoutHost.Tests;

public class LayoutRendererTests
{
    private sealed class Views : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new TextBlock { Text = "pane:" + pane.Kind };

        public Control WithTitle(string title, Control body) => body;
    }

    private sealed record Vm(string Kind, string Title) : IPaneViewModel;

    private static PaneRegistry Registry(List<string> reports)
    {
        var registry = new PaneRegistry(new Views(), reports.Add);
        registry.Register("k.ok", d => new Vm(d.Kind, d.Title));
        registry.Register("k.bad", _ => throw new InvalidOperationException("boom"));
        return registry;
    }

    private static PlacedNode Pane(string kind, string? id = null) => new(PlacedKind.Pane, kind, id, null, []);

    private static IEnumerable<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty);

    [AvaloniaFact]
    public void A_row_places_its_panes_side_by_side()
    {
        var root = (StackPanel)new LayoutRenderer(Registry([])).Render(new PlacedNode(PlacedKind.Row, null, null, null, [Pane("k.ok"), Pane("k.ok")]));

        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, root.Orientation);
        Assert.Equal(2, Texts(root).Count(t => t == "pane:k.ok"));
    }

    [AvaloniaFact]
    public void A_column_and_a_stack_place_vertically()
    {
        var renderer = new LayoutRenderer(Registry([]));

        Assert.Equal(Avalonia.Layout.Orientation.Vertical, ((StackPanel)renderer.Render(new PlacedNode(PlacedKind.Column, null, null, null, []))).Orientation);
        Assert.Equal(Avalonia.Layout.Orientation.Vertical, ((StackPanel)renderer.Render(new PlacedNode(PlacedKind.Stack, null, null, null, []))).Orientation);
    }

    [AvaloniaFact]
    public void Tabs_get_one_tab_per_child_headed_by_the_pane_id()
    {
        var tabs = (TabControl)new LayoutRenderer(Registry([])).Render(
            new PlacedNode(PlacedKind.Tabs, null, null, null, [Pane("k.ok", "first"), Pane("k.ok", "second")]));

        Assert.Equal(["first", "second"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!));
    }

    [AvaloniaFact]
    public void An_unknown_pane_kind_becomes_a_placeholder_for_that_node_only()
    {
        var root = new LayoutRenderer(Registry([])).Render(
            new PlacedNode(PlacedKind.Row, null, null, null, [Pane("k.ok"), new PlacedNode(PlacedKind.Placeholder, "k.gone", null, null, [])]));

        Assert.Single(root.GetLogicalDescendants().OfType<Border>(), b => b.Name == LayoutRenderer.PlaceholderName);
        Assert.Contains("pane:k.ok", Texts(root));
    }

    [AvaloniaFact]
    public void A_pane_that_throws_is_reported_and_replaced_by_a_placeholder_while_siblings_survive()
    {
        var reports = new List<string>();
        var root = new LayoutRenderer(Registry(reports), reports.Add).Render(
            new PlacedNode(PlacedKind.Column, null, null, null, [Pane("k.bad"), Pane("k.ok")]));

        Assert.Single(root.GetLogicalDescendants().OfType<Border>(), b => b.Name == LayoutRenderer.PlaceholderName);
        Assert.Contains("pane:k.ok", Texts(root));
        Assert.Contains(reports, r => r.Contains("k.bad", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Pane_config_reaches_the_descriptor_model()
    {
        JsonElement? seen = null;
        var registry = new PaneRegistry(new Views());
        registry.Register("k.cfg", d =>
        {
            seen = d.Model;
            return new Vm(d.Kind, d.Title);
        });
        JsonElement config = JsonElement.Parse("""{"pageSize":25}""");

        new LayoutRenderer(registry).Render(new PlacedNode(PlacedKind.Pane, "k.cfg", "p1", config, []));

        Assert.Equal(25, seen!.Value.GetProperty("pageSize").GetInt32());
    }
}
