using System.Globalization;
using Avalonia.Media;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Views;

/// <summary>
/// Resolves theme tokens from a validated theme file into Avalonia values. A view names tokens only; it never carries a raw
/// colour, font or size (doc 41c, section 5). A name that is not a required token, or that has the wrong kind, throws so the
/// node fails closed.
/// </summary>
internal sealed class ViewTheme
{
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly Dictionary<string, TokenKind> _kinds;

    public ViewTheme(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        _values = theme.Tokens;
        _kinds = ThemeTokens.Required.ToDictionary(spec => spec.Name, spec => spec.Kind, StringComparer.Ordinal);
    }

    public IBrush Brush(string token) => new SolidColorBrush(ParseColour(Raw(token, TokenKind.Colour)));

    /// <summary>
    /// Parses a theme colour. The theme file writes #RRGGBBAA, but Avalonia reads eight hex digits as #AARRGGBB, so the
    /// alpha pair moves to the front first. Without this, every translucent token would have the wrong colour.
    /// </summary>
    public static Color ParseColour(string value) =>
        value.Length == 9 ? Color.Parse("#" + value[7..9] + value[1..7]) : Color.Parse(value);

    public double Length(string token) => double.Parse(Raw(token, TokenKind.Length), CultureInfo.InvariantCulture);

    public FontFamily Family(string token) => new(Raw(token, TokenKind.FontFamily));

    private string Raw(string token, TokenKind expected)
    {
        if (!_kinds.TryGetValue(token, out TokenKind kind))
        {
            throw new ViewDeclarationException($"Unknown theme token '{token}'.");
        }

        if (kind != expected)
        {
            throw new ViewDeclarationException($"Theme token '{token}' is a {kind}, not a {expected}.");
        }

        if (!_values.TryGetValue(token, out string? value))
        {
            throw new ViewDeclarationException($"The theme does not set token '{token}'.");
        }

        return value;
    }
}
