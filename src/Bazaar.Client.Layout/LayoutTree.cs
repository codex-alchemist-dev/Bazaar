using System.Text.Json;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Rules;

namespace Bazaar.Client.Layout;

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

/// <summary>A placed tree plus the layout issues found while filtering by visibility rules. Root is null when the root node is hidden.</summary>
public sealed record LayoutPlacement(PlacedNode? Root, IReadOnlyList<LayoutIssue> Issues);

/// <summary>
/// Turns a resolved layout node into a <see cref="PlacedNode"/> tree. A pane kind that no module registers becomes a
/// placeholder card, so a newer layout never breaks an older client (doc 41 section 6, rule 7). The overload that takes a
/// <see cref="RubedoContext"/> also applies each node's <c>visibleWhen</c> rule: a hidden node is omitted with its children,
/// and a rule that errors hides the node and is reported (fail closed, doc 41a section 7).
/// </summary>
public static class LayoutTree
{
    public static PlacedNode Build(LayoutNode root, Func<string, bool> isKnownPaneKind)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isKnownPaneKind);

        return Place(root, isKnownPaneKind, null, "", [], 1)!;
    }

    /// <param name="root">The node to place.</param>
    /// <param name="isKnownPaneKind">Whether a pane kind is registered.</param>
    /// <param name="context">The device values the rules are evaluated against.</param>
    /// <param name="basePath">JSON pointer of <paramref name="root"/> in its document, the prefix of reported issue paths.</param>
    public static LayoutPlacement Build(LayoutNode root, Func<string, bool> isKnownPaneKind, RubedoContext context, string basePath = "")
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isKnownPaneKind);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(basePath);

        var issues = new List<LayoutIssue>();
        PlacedNode? placed = Place(root, isKnownPaneKind, context, basePath, issues, 1);
        return new LayoutPlacement(placed, issues);
    }

    private static PlacedNode? Place(LayoutNode node, Func<string, bool> isKnownPaneKind, RubedoContext? context, string path, List<LayoutIssue> issues, int depth)
    {
        if (context is not null && node.VisibleWhen is { } source && !IsVisible(source, context, path + "/visibleWhen", issues))
        {
            return null;
        }

        if (node.PaneKind is not null)
        {
            PlacedKind kind = isKnownPaneKind(node.PaneKind) ? PlacedKind.Pane : PlacedKind.Placeholder;
            return new PlacedNode(kind, node.PaneKind, node.PaneId, node.PaneConfig, []);
        }

        if (depth >= LayoutVocabulary.MaxNestingDepth)
        {
            return new PlacedNode(PlacedKind.Placeholder, null, node.PaneId, null, []);
        }

        var children = new List<PlacedNode>();
        for (int i = 0; i < node.Children.Count; i++)
        {
            PlacedNode? child = Place(node.Children[i], isKnownPaneKind, context, $"{path}/children/{i}", issues, depth + 1);
            if (child is not null)
            {
                children.Add(child);
            }
        }

        PlacedKind containerKind = node.Container switch
        {
            "row" => PlacedKind.Row,
            "column" => PlacedKind.Column,
            "tabs" => PlacedKind.Tabs,
            _ => PlacedKind.Stack,
        };
        return new PlacedNode(containerKind, null, node.PaneId, null, children);
    }

    private static bool IsVisible(string source, RubedoContext context, string path, List<LayoutIssue> issues)
    {
        // A document that passed the validator always compiles; a hand-built one may not, and then it fails closed too.
        (VisibilityRule? rule, IReadOnlyList<RubedoDiagnostic> diagnostics) = VisibilityRule.Compile(source);
        if (rule is null)
        {
            issues.Add(new LayoutIssue(path, "visible-when-invalid", $"visibleWhen rule rejected ({diagnostics[0].Code}): {diagnostics[0].Message}"));
            return false;
        }

        VisibilityOutcome outcome = rule.Evaluate(context);
        if (outcome.Issue is { } issue)
        {
            issues.Add(new LayoutIssue(path, "visible-when-error", $"visibleWhen rule failed ({issue.Code}): {issue.Message}"));
        }

        return outcome.Visible;
    }
}
