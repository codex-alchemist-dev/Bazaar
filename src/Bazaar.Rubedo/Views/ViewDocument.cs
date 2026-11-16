using Bazaar.Rubedo;

namespace Bazaar.Rubedo.Views;

/// <summary>
/// The closed v1 widget set (doc 41c, section 3). Adding a widget is a language version change. Wire names are the
/// lower-case spellings in <see cref="ViewWidgets.WireName"/>.
/// </summary>
public enum ViewWidget
{
    Stack,
    Row,
    Grid,
    Scroll,
    Text,
    Icon,
    Button,
    Input,
    List,
    Tabs,
    Badge,
    Divider,
    Placeholder,
}

/// <summary>The four closed actions (doc 41c, section 6).</summary>
public enum ViewActionKind
{
    Navigate,
    Invoke,
    OpenPane,
    SetSetting,
}

/// <summary>A Rubedo expression written as a prop value, in the form <c>{ "expr": "..." }</c> on the wire (doc 41c, section 2).</summary>
public sealed record ViewExpr(string Source);

/// <summary>
/// A typed data source a view reads (doc 41c, section 4). Views declare sources; they never fetch data. A scalar source
/// sets <see cref="Type"/>. A list source sets <see cref="Fields"/>: the typed fields of each record, which a keyed list
/// puts in scope in its item template. Exactly one of the two is set.
/// </summary>
public sealed record ViewDataSource(string Capability, RubedoType? Type, IReadOnlyDictionary<string, RubedoType>? Fields = null);

/// <summary>A named action the nodes refer to (doc 41c, section 6). <see cref="Target"/> is a pane, command or setting key.</summary>
public sealed record ViewAction(ViewActionKind Kind, string? Target);

/// <summary>
/// One node of a view tree (doc 41c, section 2). <see cref="Widget"/> is the wire name, so a newer document's unknown
/// widget is kept as written and reported by the validator.
/// </summary>
public sealed record ViewNode(
    string Widget,
    IReadOnlyDictionary<string, object?> Props,
    IReadOnlyList<ViewNode> Children);

/// <summary>A view document (doc 41c, section 2), schema version 1.</summary>
public sealed record ViewDocument(
    int DslVersion,
    string Name,
    IReadOnlyDictionary<string, ViewDataSource> Data,
    ViewNode Root,
    IReadOnlyDictionary<string, ViewAction> Actions);

/// <summary>Wire names of the closed widget set.</summary>
public static class ViewWidgets
{
    /// <summary>The lower-case wire spelling of a widget.</summary>
    public static string WireName(ViewWidget widget) => widget switch
    {
        ViewWidget.Stack => "stack",
        ViewWidget.Row => "row",
        ViewWidget.Grid => "grid",
        ViewWidget.Scroll => "scroll",
        ViewWidget.Text => "text",
        ViewWidget.Icon => "icon",
        ViewWidget.Button => "button",
        ViewWidget.Input => "input",
        ViewWidget.List => "list",
        ViewWidget.Tabs => "tabs",
        ViewWidget.Badge => "badge",
        ViewWidget.Divider => "divider",
        ViewWidget.Placeholder => "placeholder",
        _ => throw new ArgumentOutOfRangeException(nameof(widget), widget, null),
    };

    /// <summary>Parses a wire widget name. Returns false for a name outside the v1 set.</summary>
    public static bool TryParse(string wire, out ViewWidget widget)
    {
        foreach (ViewWidget candidate in Enum.GetValues<ViewWidget>())
        {
            if (WireName(candidate) == wire)
            {
                widget = candidate;
                return true;
            }
        }

        widget = default;
        return false;
    }
}
