using System.Text.Json;
using Avalonia.Controls;
using Bazaar.Client.Layout;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.LayoutHost;

/// <summary>
/// Builds the control tree for a placed layout (doc 41 section 7: build the tree in code from the layout document through the
/// module registry, never XAML from user data). A pane that fails to build, or a node the head cannot place, becomes a
/// placeholder card for that node only, so one bad pane never blanks the window.
/// </summary>
public sealed class LayoutRenderer
{
    /// <summary>The <see cref="Control.Name"/> of every placeholder card.</summary>
    public const string PlaceholderName = "layout-placeholder";

    private readonly PaneRegistry _registry;
    private readonly Action<string>? _report;

    public LayoutRenderer(PaneRegistry registry, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _report = report;
    }

    public Control Render(PlacedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return Place(node);
    }

    private Control Place(PlacedNode node) => node.Kind switch
    {
        PlacedKind.Row => Container(node, Avalonia.Layout.Orientation.Horizontal),
        PlacedKind.Column or PlacedKind.Stack => Container(node, Avalonia.Layout.Orientation.Vertical),
        PlacedKind.Tabs => Tabs(node),
        PlacedKind.Pane => Pane(node),
        _ => Placeholder(node.PaneKind ?? node.PaneId ?? "unknown", "No module provides this pane."),
    };

    private StackPanel Container(PlacedNode node, Avalonia.Layout.Orientation orientation)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = 8 };
        foreach (PlacedNode child in node.Children)
        {
            panel.Children.Add(Place(child));
        }

        return panel;
    }

    private TabControl Tabs(PlacedNode node)
    {
        var tabs = new TabControl();
        var items = new List<TabItem>();
        foreach (PlacedNode child in node.Children)
        {
            items.Add(new TabItem { Header = child.PaneId ?? child.PaneKind ?? "tab", Content = Place(child) });
        }

        tabs.ItemsSource = items;
        return tabs;
    }

    private Control Pane(PlacedNode node)
    {
        string kind = node.PaneKind!;
        try
        {
            JsonElement model = node.PaneConfig ?? JsonElement.Parse("{}");
            var descriptor = new PaneDescriptor(kind, node.PaneId ?? kind, string.Empty, model);
            return _registry.CreateView(_registry.Create(descriptor));
        }
        catch (Exception ex)
        {
            _report?.Invoke($"layout: pane '{kind}' failed to build: {ex.Message}");
            return Placeholder(kind, "This pane could not be built.");
        }
    }

    private static Border Placeholder(string what, string reason) => new()
    {
        Name = PlaceholderName,
        Padding = new Avalonia.Thickness(8),
        Child = new TextBlock { Text = $"{what}: {reason}" },
    };
}
