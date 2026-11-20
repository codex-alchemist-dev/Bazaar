using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

/// <summary>
/// Maps a view declaration to Avalonia controls, one method per widget kind (doc 41c, section 3). A node that cannot be
/// mapped (unknown widget, missing prop, unknown token or action) becomes a visible placeholder in its own place, so its
/// siblings still render (doc 41 section 7: fail closed, never crash).
/// </summary>
public sealed partial class ViewMapper
{
    /// <summary>The value of <see cref="Control.Name"/> on every placeholder card, so heads and tests can find them.</summary>
    public const string PlaceholderName = "view-placeholder";

    private readonly ViewContext _context;
    private readonly ViewTheme _theme;

    public ViewMapper(ViewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        _theme = new ViewTheme(context.Theme);
    }

    /// <summary>Builds the control tree for <paramref name="document"/>. <paramref name="data"/> is the top-level scope.</summary>
    public Control Build(ViewDocument document, IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(data);

        return Node(document.Root, new Scope(document, data, SchemaOf(document)));
    }

    internal Control Node(ViewNode node, Scope scope)
    {
        try
        {
            Control control = Widget(node, scope);
            ApplyConditions(control, node, scope);
            return control;
        }
        catch (ViewDeclarationException ex)
        {
            return Placeholder(scope.Document.Name, node.Widget, ex.Message);
        }
    }

    /// <summary>
    /// Applies the two conditions a node may carry (doc 41a, section 1; doc 41c, section 8): <c>visibleWhen</c> shows or hides
    /// the control and <c>enabledWhen</c> enables or disables it. Each is a Rubedo expression of type bool, or a literal bool,
    /// evaluated against the node's scope (so a list item's template sees its own fields). A value that is not a bool fails
    /// the node closed, so a bad condition never leaves a control visible by accident.
    /// </summary>
    private static void ApplyConditions(Control control, ViewNode node, Scope scope)
    {
        if (node.Props.TryGetValue("visibleWhen", out object? visible))
        {
            control.IsVisible = Condition(visible, "visibleWhen", scope);
        }

        if (node.Props.TryGetValue("enabledWhen", out object? enabled))
        {
            control.IsEnabled = Condition(enabled, "enabledWhen", scope);
        }
    }

    private static bool Condition(object? raw, string name, Scope scope) =>
        Resolve(raw, scope) is bool flag
            ? flag
            : throw new ViewDeclarationException($"prop '{name}' must be a bool.");

    private Control Widget(ViewNode node, Scope scope) => node.Widget switch
    {
        "stack" => MapStack(node, scope),
        "row" => MapRow(node, scope),
        "grid" => MapGrid(node, scope),
        "scroll" => MapScroll(node, scope),
        "divider" => MapDivider(),
        "text" => MapText(node, scope),
        "icon" => MapIcon(node, scope),
        "button" => MapButton(node, scope),
        "input" => MapInput(node, scope),
        "badge" => MapBadge(node, scope),
        "list" => MapList(node, scope),
        "tabs" => MapTabs(node, scope),
        "placeholder" => MapPlaceholder(node, scope),
        _ => throw new ViewDeclarationException("unknown widget."),
    };

    /// <summary>The visible failure card. It shows the widget name and the reason as plain text, never as markup.</summary>
    internal Border Placeholder(string viewName, string widget, string reason) => new Border
    {
        Name = PlaceholderName,
        BorderBrush = _theme.Brush("color.border.default"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(_theme.Length("radius.sm")),
        Padding = new Thickness(_theme.Length("space.sm")),
        Child = new TextBlock
        {
            Text = $"Element '{widget}' is not shown in view '{viewName}': {reason}",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = _theme.Family("font.family.ui"),
            FontSize = _theme.Length("font.size.small"),
            Foreground = _theme.Brush("color.text.secondary"),
        },
    };

    private Border MapPlaceholder(ViewNode node, Scope scope)
    {
        string reason = node.Props.ContainsKey("message")
            ? Text(node, "message", scope)
            : "this element is not available in this version.";

        return Placeholder(scope.Document.Name, node.Widget, reason);
    }

    /// <summary>
    /// A scope is the document data at the top, or one item inside a list. <see cref="Schema"/> is the Rubedo context its
    /// expressions compile against: the document's scalar data at the top, plus the item's typed fields inside a list template.
    /// </summary>
    internal sealed record Scope(ViewDocument Document, IReadOnlyDictionary<string, object?> Values, RubedoSchema Schema);
}
