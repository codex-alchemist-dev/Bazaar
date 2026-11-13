using System.Text.Json;

namespace Bazaar.Client.Core.Layout;

/// <summary>What a placed node renders as. Unknown containers stack their children (doc 41 section 7).</summary>
public enum PlacedKind
{
    Row,
    Column,
    Tabs,
    Stack,
    Pane,
    Placeholder,
}

/// <summary>A node ready for a head to render. Pure data; the head maps it to Avalonia controls.</summary>
public sealed record PlacedNode(PlacedKind Kind, string? PaneKind, string? PaneId, JsonElement? PaneConfig, IReadOnlyList<PlacedNode> Children);

/// <summary>
/// Turns a resolved layout node into a <see cref="PlacedNode"/> tree. A pane kind that no module registers becomes a
/// placeholder card, so a newer layout never breaks an older client (doc 41 section 6, rule 7).
/// </summary>
public static class LayoutTree
{
    public static PlacedNode Build(LayoutNode root, Func<string, bool> isKnownPaneKind)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isKnownPaneKind);

        return Place(root, isKnownPaneKind, 1);
    }

    private static PlacedNode Place(LayoutNode node, Func<string, bool> isKnownPaneKind, int depth)
    {
        if (node.PaneKind is not null)
        {
            PlacedKind kind = isKnownPaneKind(node.PaneKind) ? PlacedKind.Pane : PlacedKind.Placeholder;
            return new PlacedNode(kind, node.PaneKind, node.PaneId, node.PaneConfig, []);
        }

        if (depth >= LayoutVocabulary.MaxNestingDepth)
        {
            return new PlacedNode(PlacedKind.Placeholder, null, node.PaneId, null, []);
        }

        var children = node.Children.Select(child => Place(child, isKnownPaneKind, depth + 1)).ToList();
        PlacedKind containerKind = node.Container switch
        {
            "row" => PlacedKind.Row,
            "column" => PlacedKind.Column,
            "tabs" => PlacedKind.Tabs,
            _ => PlacedKind.Stack,
        };
        return new PlacedNode(containerKind, null, node.PaneId, null, children);
    }
}
