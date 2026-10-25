namespace Bazaar.Client.Core.Theme;

/// <summary>The kinds of value a token may hold in a schemaVersion 1 theme file.</summary>
public enum TokenKind
{
    /// <summary>#RRGGBB or #RRGGBBAA.</summary>
    Colour,

    /// <summary>A font family name: letters, digits, spaces, underscores and hyphens, starting with a letter.</summary>
    FontFamily,

    /// <summary>An integer from 0 to 256 (pixels).</summary>
    Length,

    /// <summary>true or false.</summary>
    Flag,
}

/// <summary>A required token and its kind.</summary>
public sealed record TokenSpec(string Name, TokenKind Kind);

/// <summary>A foreground token checked against a background token at 4.5:1 or better (WCAG 2.2 AA text).</summary>
public sealed record ContrastPair(string Foreground, string Background);

/// <summary>
/// The required token list for schemaVersion 1 (docs/design/27-client-architecture.md, Theme file).
/// Every token below must be present; unknown tokens are rejected so a typo cannot pass silently.
/// </summary>
public static class ThemeTokens
{
    /// <summary>The only schema version this build reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>WCAG 2.2 AA minimum for body text.</summary>
    public const double MinimumContrastRatio = 4.5;

    /// <summary>Largest accepted length token value, in pixels.</summary>
    public const int MaxLength = 256;

    public static readonly IReadOnlyList<TokenSpec> Required =
    [
        new("color.background.base", TokenKind.Colour),
        new("color.background.surface", TokenKind.Colour),
        new("color.background.editor", TokenKind.Colour),
        new("color.text.primary", TokenKind.Colour),
        new("color.text.secondary", TokenKind.Colour),
        new("color.text.accent", TokenKind.Colour),
        new("color.border.default", TokenKind.Colour),
        new("color.accent.default", TokenKind.Colour),
        new("color.badge.owner.background", TokenKind.Colour),
        new("color.badge.owner.text", TokenKind.Colour),
        new("color.badge.agent.background", TokenKind.Colour),
        new("color.badge.agent.text", TokenKind.Colour),
        new("color.badge.system.background", TokenKind.Colour),
        new("color.badge.system.text", TokenKind.Colour),
        new("color.syntax.keyword", TokenKind.Colour),
        new("color.syntax.string", TokenKind.Colour),
        new("color.syntax.comment", TokenKind.Colour),
        new("color.syntax.number", TokenKind.Colour),
        new("color.syntax.type", TokenKind.Colour),
        new("color.syntax.default", TokenKind.Colour),
        new("font.family.ui", TokenKind.FontFamily),
        new("font.family.mono", TokenKind.FontFamily),
        new("font.size.body", TokenKind.Length),
        new("font.size.small", TokenKind.Length),
        new("space.xs", TokenKind.Length),
        new("space.sm", TokenKind.Length),
        new("space.md", TokenKind.Length),
        new("space.lg", TokenKind.Length),
        new("radius.sm", TokenKind.Length),
        new("radius.md", TokenKind.Length),
        new("reducedMotion", TokenKind.Flag),
    ];

    /// <summary>
    /// Text pairs checked for contrast. Every pair must be opaque and reach MinimumContrastRatio.
    /// Syntax colours are checked against the editor background because Monaco paints them there.
    /// </summary>
    public static readonly IReadOnlyList<ContrastPair> ContrastPairs =
    [
        new("color.text.primary", "color.background.base"),
        new("color.text.primary", "color.background.surface"),
        new("color.text.primary", "color.background.editor"),
        new("color.text.secondary", "color.background.base"),
        new("color.text.secondary", "color.background.surface"),
        new("color.text.accent", "color.background.base"),
        new("color.badge.owner.text", "color.badge.owner.background"),
        new("color.badge.agent.text", "color.badge.agent.background"),
        new("color.badge.system.text", "color.badge.system.background"),
        new("color.syntax.keyword", "color.background.editor"),
        new("color.syntax.string", "color.background.editor"),
        new("color.syntax.comment", "color.background.editor"),
        new("color.syntax.number", "color.background.editor"),
        new("color.syntax.type", "color.background.editor"),
        new("color.syntax.default", "color.background.editor"),
    ];
}
