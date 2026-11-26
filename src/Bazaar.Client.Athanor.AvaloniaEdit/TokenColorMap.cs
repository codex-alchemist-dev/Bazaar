using System.Text.Json;
using Avalonia.Media;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// The token colour rules of a Monaco defineTheme payload (doc 57 §2). Scopes resolve by longest dotted prefix, so
/// <c>keyword.control</c> falls back to <c>keyword</c>; the empty token is the default foreground.
/// </summary>
public sealed class TokenColorMap
{
    // Order matters: "XmlComment" is a comment, "ValueTypeKeywords" is a type, "MethodCall" is a function.
    private static readonly (string Needle, string Scope)[] _scopes =
    [
        ("comment", "comment"),
        ("doc", "comment"),
        ("string", "string"),
        ("char", "string"),
        ("number", "number"),
        ("digit", "number"),
        ("preprocessor", "keyword.preprocessor"),
        ("attribute", "type.attribute"),
        ("tag", "keyword.tag"),
        ("type", "type"),
        ("method", "function"),
        ("function", "function"),
        ("keyword", "keyword"),
        ("visibility", "keyword"),
        ("namespace", "keyword"),
        ("modifier", "keyword"),
        ("truefalse", "keyword"),
        ("this", "keyword"),
        ("null", "keyword"),
    ];

    private readonly Dictionary<string, Color> _rules;

    private TokenColorMap(Dictionary<string, Color> rules, Color? background)
    {
        _rules = rules;
        Background = background;
    }

    /// <summary>The editor background of the theme, when it has one.</summary>
    public Color? Background { get; }

    /// <summary>The default foreground (the rule with an empty token), when the theme has one.</summary>
    public Color? Default => _rules.TryGetValue(string.Empty, out Color color) ? color : null;

    /// <summary>The number of rules read.</summary>
    public int Count => _rules.Count;

    /// <summary>Parses the payload. Throws <see cref="FormatException"/> when it is not a JSON object. Bad rules are skipped.</summary>
    public static TokenColorMap Parse(string monacoThemeJson)
    {
        ArgumentNullException.ThrowIfNull(monacoThemeJson);
        try
        {
            using JsonDocument document = JsonDocument.Parse(monacoThemeJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The theme payload is not a JSON object.");
            }

            var rules = new Dictionary<string, Color>(StringComparer.Ordinal);
            if (root.TryGetProperty("rules", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement rule in array.EnumerateArray())
                {
                    if (rule.ValueKind == JsonValueKind.Object
                        && rule.TryGetProperty("foreground", out JsonElement foreground)
                        && foreground.ValueKind == JsonValueKind.String
                        && TryColor(foreground.GetString(), out Color color))
                    {
                        string token = rule.TryGetProperty("token", out JsonElement t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString() ?? string.Empty
                            : string.Empty;
                        rules[token] = color;
                    }
                }
            }

            Color? background = null;
            if (root.TryGetProperty("colors", out JsonElement colors)
                && colors.ValueKind == JsonValueKind.Object
                && colors.TryGetProperty("editor.background", out JsonElement bg)
                && bg.ValueKind == JsonValueKind.String
                && TryColor(bg.GetString(), out Color parsed))
            {
                background = parsed;
            }

            return new TokenColorMap(rules, background);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The theme payload is not valid JSON.", exception);
        }
    }

    /// <summary>Resolves a scope (for example <c>comment.line</c>) to a colour by longest prefix, or null when no rule applies (the empty token does not count).</summary>
    public Color? Resolve(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        string current = scope;
        while (current.Length > 0)
        {
            if (_rules.TryGetValue(current, out Color color))
            {
                return color;
            }

            int dot = current.LastIndexOf('.');
            current = dot < 0 ? string.Empty : current[..dot];
        }

        return null;
    }

    /// <summary>The Monaco scope for an AvaloniaEdit named colour, or null when the name maps to no scope.</summary>
    public static string? ScopeFor(string? colourName)
    {
        if (string.IsNullOrEmpty(colourName))
        {
            return null;
        }

        foreach ((string needle, string scope) in _scopes)
        {
            if (colourName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return scope;
            }
        }

        return null;
    }

    private static bool TryColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.StartsWith('#') ? text : "#" + text;
        return Color.TryParse(value, out color);
    }
}
