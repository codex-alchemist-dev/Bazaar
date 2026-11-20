using Bazaar.Rubedo;
using Bazaar.Rubedo.Semantics;

namespace Bazaar.Rubedo.Views;

/// <summary>
/// Validates a view document (doc 41c, section 8). Pure: it checks structure, labels, actions, declared data and
/// expressions, and never renders or fetches anything. Problems come back as diagnostics with a stable code.
/// </summary>
public static class ViewValidator
{
    /// <summary>The only schema version this validator accepts (doc 41c, section 2).</summary>
    public const int SupportedDslVersion = 1;

    private static readonly HashSet<ViewWidget> _leaves =
    [
        ViewWidget.Text,
        ViewWidget.Icon,
        ViewWidget.Button,
        ViewWidget.Input,
        ViewWidget.Badge,
        ViewWidget.Divider,
        ViewWidget.Placeholder,
    ];

    private static readonly HashSet<ViewWidget> _needsLabel = [ViewWidget.Button, ViewWidget.Icon, ViewWidget.Input];

    /// <summary>Returns every problem found. An empty list means the document is valid.</summary>
    public static IReadOnlyList<RubedoDiagnostic> Validate(ViewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var problems = new List<RubedoDiagnostic>();
        if (document.DslVersion != SupportedDslVersion)
        {
            problems.Add(new RubedoDiagnostic("view.dsl_version", $"Unsupported dslVersion {document.DslVersion}; this head reads version {SupportedDslVersion}."));
        }

        if (!IsDottedName(document.Name))
        {
            problems.Add(new RubedoDiagnostic("view.bad_name", $"View name '{document.Name}' must be dotted lower-case segments, such as 'bazaar.gc.feed'."));
        }

        var scope = BuildScope(document, problems);
        foreach (var (id, action) in document.Actions)
        {
            if (!Enum.IsDefined(action.Kind))
            {
                problems.Add(new RubedoDiagnostic("view.unknown_action_kind", $"Action '{id}' has an unknown kind."));
            }
        }

        Walk(document.Root, "root", document, scope, problems);
        return problems;
    }

    /// <summary>
    /// The names in scope at the root: the context identifiers plus every scalar data source. List sources are not
    /// expression names; a keyed list brings their fields into scope inside its template.
    /// </summary>
    private static RubedoSchema BuildScope(ViewDocument document, List<RubedoDiagnostic> problems)
    {
        var identifiers = new Dictionary<string, RubedoType>(RubedoSchema.Standard.Identifiers, StringComparer.Ordinal);
        foreach (var (name, source) in document.Data)
        {
            if (!IsIdentifier(name))
            {
                problems.Add(new RubedoDiagnostic("view.bad_data_name", $"Data source '{name}' is not a valid identifier."));
                continue;
            }

            if (identifiers.ContainsKey(name))
            {
                problems.Add(new RubedoDiagnostic("view.data_shadows_context", $"Data source '{name}' shadows a context identifier."));
                continue;
            }

            if (source.Type is { } scalar)
            {
                identifiers[name] = scalar;
            }
        }

        return new RubedoSchema(identifiers);
    }

    /// <summary>The structural child of <c>tabs</c>. It is not a widget, so the closed set stays at 13 (doc 41c, section 3).</summary>
    private static readonly string _tabNode = "tab";

    private static void Walk(ViewNode node, string path, ViewDocument document, RubedoSchema scope, List<RubedoDiagnostic> problems)
    {
        if (node.Widget == _tabNode)
        {
            problems.Add(new RubedoDiagnostic("view.tab_placement", $"{path}: a tab is only valid as a child of tabs."));
            return;
        }

        if (!ViewWidgets.TryParse(node.Widget, out var widget))
        {
            problems.Add(new RubedoDiagnostic("view.unknown_widget", $"{path}: widget '{node.Widget}' is not in the v1 set."));
            return;
        }

        if (_needsLabel.Contains(widget) && !HasLabel(node))
        {
            problems.Add(new RubedoDiagnostic("view.missing_label", $"{path}: {node.Widget} needs a label (a localisation key or literal)."));
        }

        if (widget == ViewWidget.Button && node.Props.TryGetValue("action", out var actionRef))
        {
            if (actionRef is not string actionId || !document.Actions.ContainsKey(actionId))
            {
                problems.Add(new RubedoDiagnostic("view.unknown_action", $"{path}: button refers to an action that is not declared."));
            }
        }

        foreach (var (propName, value) in node.Props)
        {
            if (value is ViewExpr expr)
            {
                CheckExpression(expr, propName, path, scope, problems);
            }
            else if (value is ViewArgs args)
            {
                CheckArgs(args, widget, path, scope, problems);
            }
        }

        if (_leaves.Contains(widget) && node.Children.Count > 0)
        {
            problems.Add(new RubedoDiagnostic("view.unexpected_children", $"{path}: {node.Widget} takes no children."));
        }

        if (widget == ViewWidget.Tabs)
        {
            CheckTabs(node, path, document, scope, problems);
            return;
        }

        var childScope = scope;
        if (widget == ViewWidget.List)
        {
            if (node.Children.Count != 1)
            {
                problems.Add(new RubedoDiagnostic("view.list_template", $"{path}: a list takes exactly one item template (doc 41c, section 9.4)."));
            }

            childScope = CheckList(node, path, document, scope, problems) ?? scope;
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            Walk(node.Children[i], $"{path}/{i}", document, childScope, problems);
        }
    }

    /// <summary>
    /// Checks a tab set: at least one child, every child a <c>tab</c>. Each tab has a non-empty <c>title</c> and exactly
    /// one page child, which is walked like any other node.
    /// </summary>
    private static void CheckTabs(ViewNode node, string path, ViewDocument document, RubedoSchema scope, List<RubedoDiagnostic> problems)
    {
        if (node.Children.Count == 0)
        {
            problems.Add(new RubedoDiagnostic("view.tabs_empty", $"{path}: tabs needs at least one tab."));
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            var tab = node.Children[i];
            var tabPath = $"{path}/{i}";
            if (tab.Widget != _tabNode)
            {
                problems.Add(new RubedoDiagnostic("view.tab_child", $"{tabPath}: a child of tabs must be a tab, not '{tab.Widget}'."));
                continue;
            }

            if (!(tab.Props.TryGetValue("title", out var title) && title is string text && text.Length > 0))
            {
                problems.Add(new RubedoDiagnostic("view.tab_title", $"{tabPath}: a tab needs a title (a localisation key or literal)."));
            }

            if (tab.Children.Count != 1)
            {
                problems.Add(new RubedoDiagnostic("view.tab_page", $"{tabPath}: a tab takes exactly one page child."));
            }

            for (var j = 0; j < tab.Children.Count; j++)
            {
                Walk(tab.Children[j], $"{tabPath}/{j}", document, scope, problems);
            }
        }
    }

    /// <summary>
    /// Checks a keyed list: <c>over</c> names a list source, <c>key</c> names one of its fields, and the fields come into
    /// scope in the template. Returns the template's scope, or null when the list's source is not usable.
    /// </summary>
    private static RubedoSchema? CheckList(ViewNode node, string path, ViewDocument document, RubedoSchema scope, List<RubedoDiagnostic> problems)
    {
        if (!node.Props.TryGetValue("over", out var overValue) || overValue is not string overName ||
            !document.Data.TryGetValue(overName, out var source) || source.Fields is null)
        {
            problems.Add(new RubedoDiagnostic("view.list_source", $"{path}: a list's 'over' must name a declared list source."));
            return null;
        }

        if (!node.Props.TryGetValue("key", out var keyValue) || keyValue is not string keyName || !source.Fields.ContainsKey(keyName))
        {
            problems.Add(new RubedoDiagnostic("view.list_key", $"{path}: a list's 'key' must name a field of '{overName}'."));
        }

        var itemIdentifiers = new Dictionary<string, RubedoType>(scope.Identifiers, StringComparer.Ordinal);
        foreach (var (field, type) in source.Fields)
        {
            if (!IsIdentifier(field))
            {
                problems.Add(new RubedoDiagnostic("view.bad_field_name", $"{path}: field '{field}' of '{overName}' is not a valid identifier."));
                continue;
            }

            if (scope.Identifiers.ContainsKey(field))
            {
                problems.Add(new RubedoDiagnostic("view.item_field_shadows", $"{path}: field '{field}' of '{overName}' shadows a name in scope."));
                continue;
            }

            itemIdentifiers[field] = type;
        }

        return new RubedoSchema(itemIdentifiers);
    }

    /// <summary>Checks button arguments (doc 41c, section 6.1): buttons only, at most eight, identifier names, scalar values, expressions that compile in scope.</summary>
    private static void CheckArgs(ViewArgs args, ViewWidget widget, string path, RubedoSchema scope, List<RubedoDiagnostic> problems)
    {
        if (widget != ViewWidget.Button)
        {
            problems.Add(new RubedoDiagnostic("view.args_widget", $"{path}: only a button takes args."));
            return;
        }

        if (args.Values.Count > ViewArgs.MaxCount)
        {
            problems.Add(new RubedoDiagnostic("view.args_count", $"{path}: a button takes at most {ViewArgs.MaxCount} args."));
        }

        foreach (var (name, value) in args.Values)
        {
            if (!IsIdentifier(name))
            {
                problems.Add(new RubedoDiagnostic("view.args_name", $"{path}: argument '{name}' is not a valid identifier."));
            }

            switch (value)
            {
                case ViewExpr expr:
                    CheckExpression(expr, $"args.{name}", path, scope, problems);
                    break;
                case string or bool or double:
                    break;
                default:
                    problems.Add(new RubedoDiagnostic("view.args_value", $"{path}: argument '{name}' must be a text, number or bool literal, or an expression."));
                    break;
            }
        }
    }

    private static void CheckExpression(ViewExpr expr, string propName, string path, RubedoSchema scope, List<RubedoDiagnostic> problems)
    {
        var requiredType = propName is "visibleWhen" or "enabledWhen" ? RubedoType.Bool : (RubedoType?)null;
        var compiled = RubedoEngine.Compile(expr.Source, scope, requiredType);
        foreach (var diagnostic in compiled.Diagnostics)
        {
            problems.Add(diagnostic with { Message = $"{path}.{propName}: {diagnostic.Message}" });
        }
    }

    private static bool HasLabel(ViewNode node) =>
        node.Props.TryGetValue("label", out var label) && label is string text && text.Length > 0;

    private static bool IsDottedName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var segments = name.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !segment.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c));
}
