using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.Theme;
using Bazaar.Rubedo;
using Bazaar.Rubedo.Views;
using TokenKind = Bazaar.Client.Theme.TokenKind;

namespace Bazaar.Client.Views.Tests.Fixtures;

/// <summary>Builders for real view documents, nodes and a theme. Documents use the Rubedo view schema (doc 41c, sections 2 and 3).</summary>
internal static class ViewFixtures
{
    public const string ColourText = "#FFFFFF";
    public const string ColourSecondary = "#AAAAAA";
    public const string ColourSurface = "#000011";

    public static ThemeDefinition Theme(bool includeTextPrimary = true)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TokenSpec spec in ThemeTokens.Required)
        {
            tokens[spec.Name] = spec.Kind switch
            {
                TokenKind.Colour => "#1E2A3B",
                TokenKind.FontFamily => "Inter",
                TokenKind.Length => spec.Name switch
                {
                    "font.size.body" => "14",
                    "font.size.small" => "12",
                    "space.xs" => "4",
                    "space.sm" => "8",
                    "radius.sm" => "3",
                    _ => "6",
                },
                _ => "false",
            };
        }

        tokens["color.text.primary"] = ColourText;
        tokens["color.text.secondary"] = ColourSecondary;
        tokens["color.background.surface"] = ColourSurface;
        if (!includeTextPrimary)
        {
            tokens.Remove("color.text.primary");
        }

        return new ThemeDefinition(ThemeTokens.SchemaVersion, "test", ThemeBase.Dark, tokens);
    }

    /// <summary>A node with its props. A prop value is a literal (string, bool or number) or a <see cref="ViewExpr"/> from <see cref="Bind"/>.</summary>
    public static ViewNode Node(string widget, ViewNode[]? children = null, params (string Name, object? Value)[] props) =>
        new(widget, props.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal), children ?? []);

    public static object Lit(object value) => value;

    /// <summary>A Rubedo expression prop. A bare identifier names a field of the scope; anything else is evaluated.</summary>
    public static ViewExpr Bind(string source) => new(source);

    public static ViewDocument Document(ViewNode root, params (string Id, ViewAction Action)[] actions) =>
        new(ViewValidator.SupportedDslVersion, "bazaar.test.view", new Dictionary<string, ViewDataSource>(StringComparer.Ordinal), root, actions.ToDictionary(a => a.Id, a => a.Action, StringComparer.Ordinal));

    /// <summary>One valid node for each of the 13 widget kinds, so every kind can be checked the same way.</summary>
    public static ViewDocument Minimal(string widget)
    {
        var actions = new (string, ViewAction)[]
        {
            ("open", new ViewAction(ViewActionKind.Navigate, "gc.feed")),
            ("set-name", new ViewAction(ViewActionKind.SetSetting, "name")),
        };
        ViewNode root = widget switch
        {
            "stack" => Node("stack", [Text("hello")]),
            "row" => Node("row", [Text("a"), Text("b")]),
            "grid" => Node("grid", [Text("a"), Text("b"), Text("c")], ("columns", Lit(2.0))),
            "scroll" => Node("scroll", [Text("long")]),
            "text" => Text("hello"),
            "icon" => Node("icon", [], ("name", Lit("check")), ("label", Lit("Done"))),
            "button" => Node("button", [], ("label", Lit("Open")), ("action", Lit("open"))),
            "input" => Node("input", [], ("label", Lit("Name")), ("value", Lit("")), ("onChange", Lit("set-name"))),
            "list" => Node("list", [Node("text", [], ("value", Bind("title")))], ("over", Lit("items")), ("key", Lit("id"))),
            "tabs" => Node("tabs", [Node("tab", [Text("page")], ("title", Lit("Tab one")))]),
            "badge" => Node("badge", [], ("value", Lit("3")), ("variant", Lit("owner"))),
            "divider" => Node("divider"),
            "placeholder" => Node("placeholder", [], ("message", Lit("Coming later."))),
            _ => throw new ArgumentOutOfRangeException(nameof(widget), widget, "Not one of the 13 widgets."),
        };

        return widget == "list" ? ListDocument(root) : Document(root, actions);
    }

    /// <summary>A document with one declared list source, "items", whose records have a text id and a text title.</summary>
    public static ViewDocument ListDocument(ViewNode root)
    {
        var fields = new Dictionary<string, RubedoType>(StringComparer.Ordinal) { ["id"] = RubedoType.Text, ["title"] = RubedoType.Text };
        var data = new Dictionary<string, ViewDataSource>(StringComparer.Ordinal) { ["items"] = new("fixture:items", null, fields) };
        return new ViewDocument(ViewValidator.SupportedDslVersion, "bazaar.test.view", data, root, new Dictionary<string, ViewAction>());
    }

    public static ViewNode Text(string text) => Node("text", [], ("value", Lit(text)));

    public static Dictionary<string, object?> Data(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    public static Color ColorOf(IBrush? brush) => ((SolidColorBrush)brush!).Color;

    public static Color Parse(string hex) => Color.Parse(hex);

    /// <summary>The placeholder card created by the mapper for a failing node, or null when the control is not one.</summary>
    public static Border? AsPlaceholder(Control control) =>
        control is Border border && border.Name == ViewMapper.PlaceholderName ? border : null;

    public static string PlaceholderText(Control control) =>
        ((TextBlock)AsPlaceholder(control)!.Child!).Text!;
}
