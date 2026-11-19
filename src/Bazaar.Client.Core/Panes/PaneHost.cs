using Avalonia.Controls;
using Avalonia.Layout;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Builds the stacked host for a list of panes. Each descriptor goes through the registry, so an unknown
/// kind or a bad model renders the generic view instead of failing the window.
/// </summary>
public static class PaneHost
{
    public static Control Build(IEnumerable<PaneDescriptor> descriptors, PaneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(registry);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 24, Margin = new Avalonia.Thickness(16) };
        foreach (var descriptor in descriptors)
        {
            stack.Children.Add(registry.CreateView(registry.Create(descriptor)));
        }

        return stack;
    }
}
