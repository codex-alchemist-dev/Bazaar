using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Client.Core.Theme;

/// <summary>
/// Emits a Monaco defineTheme payload as JSON data from a validated theme. Pure data: no WebView or Monaco runtime here.
/// Rule foreground values drop the leading # (Monaco's rule format); editor colours keep it.
/// </summary>
public static class MonacoThemeGenerator
{
    private static readonly (string Token, string Key)[] _syntaxRules =
    [
        ("", "color.syntax.default"),
        ("keyword", "color.syntax.keyword"),
        ("string", "color.syntax.string"),
        ("comment", "color.syntax.comment"),
        ("number", "color.syntax.number"),
        ("type", "color.syntax.type"),
    ];

    public static string Generate(ThemeDefinition theme)
    {
        var rules = new JsonArray();
        foreach ((string token, string key) in _syntaxRules)
        {
            rules.Add(new JsonObject
            {
                ["token"] = token,
                ["foreground"] = theme.Tokens[key].TrimStart('#'),
            });
        }

        var colors = new JsonObject
        {
            ["editor.background"] = theme.Tokens["color.background.editor"],
            ["editor.foreground"] = theme.Tokens["color.syntax.default"],
            ["editorCursor.foreground"] = theme.Tokens["color.accent.default"],
        };

        var root = new JsonObject
        {
            ["base"] = theme.Base == ThemeBase.Dark ? "vs-dark" : "vs",
            ["inherit"] = true,
            ["rules"] = rules,
            ["colors"] = colors,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
