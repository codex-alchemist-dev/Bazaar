namespace Bazaar.Client.Theme;

/// <summary>The kinds of value a token may hold in a theme file.</summary>
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

    /// <summary>A JSON number within the token's Min and Max (schemaVersion 2). Canonical form has at most three decimals.</summary>
    Number,

    /// <summary>A duration in whole milliseconds, 0 to <see cref="ThemeTokens.MaxDurationMs"/> (schemaVersion 2).</summary>
    Duration,

    /// <summary>One of the token's fixed string choices (schemaVersion 2).</summary>
    Choice,
}

/// <summary>
/// A required token and its kind. Since is the schema version that introduced it; Min and Max bound a Number;
/// Choices lists the allowed values of a Choice.
/// </summary>
public sealed record TokenSpec(
    string Name,
    TokenKind Kind,
    int Since = 1,
    double Min = 0,
    double Max = 0,
    IReadOnlyList<string>? Choices = null);

/// <summary>A foreground token checked against a background token at 4.5:1 or better (WCAG 2.2 AA text).</summary>
public sealed record ContrastPair(string Foreground, string Background);

/// <summary>
/// The required token list (docs/design/27-client-architecture.md, Theme file; docs/design/93-client-visual-system-and-motion.md,
/// Theme file additions). schemaVersion 2 is additive: every schemaVersion 1 token is unchanged and a version 1 file is
/// migrated by filling the version 2 tokens with defaults (<see cref="ThemeDefaults"/>). A file must hold exactly the tokens
/// of its own version, so unknown tokens are rejected and a typo cannot pass silently.
/// </summary>
public static class ThemeTokens
{
    /// <summary>The current schema version; every loaded theme is migrated to it.</summary>
    public const int SchemaVersion = 2;

    /// <summary>The oldest schema version this build still reads (and migrates).</summary>
    public const int MinSchemaVersion = 1;

    /// <summary>Number of explicit hue tokens, color.hue.0 to color.hue.11.</summary>
    public const int HueCount = 12;

    /// <summary>Largest accepted duration token value, in milliseconds.</summary>
    public const int MaxDurationMs = 5000;

    /// <summary>
    /// Safety floor (doc 93): the reduced-motion cross-fade is at most this long. A theme may shorten it, never lengthen it.
    /// </summary>
    public const int ReducedDurationFloorMs = 90;

    /// <summary>Contrast floor for graphics (WCAG 2.2 non-text contrast).</summary>
    public const double MinimumGraphicContrastRatio = 3.0;

    /// <summary>The colours the hue family must pass against, as text (4.5:1) and as a graphic (3:1).</summary>
    public static readonly IReadOnlyList<string> HueBackgrounds =
        ["color.background.base", "color.background.surface", "color.background.editor"];

    /// <summary>Text tokens that must stay readable on a translucent material at the worst-case backdrop.</summary>
    public static readonly IReadOnlyList<string> MaterialTextTokens =
        ["color.text.primary", "color.text.secondary", "color.text.accent"];

    /// <summary>The name of hue token <paramref name="index"/>, 0 to 11.</summary>
    public static string HueToken(int index) => "color.hue." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The schemaVersion 1 tokens, unchanged.</summary>
    public static IEnumerable<TokenSpec> V1 => Required.Where(spec => spec.Since == 1);

    /// <summary>The tokens a file of the given schema version must hold.</summary>
    public static IEnumerable<TokenSpec> For(int schemaVersion) => Required.Where(spec => spec.Since <= schemaVersion);

    /// <summary>WCAG 2.2 AA minimum for body text.</summary>
    public const double MinimumContrastRatio = 4.5;

    /// <summary>Largest accepted length token value, in pixels.</summary>
    public const int MaxLength = 256;

    /// <summary>Allowed values of material.mode; they mirror the client.materials setting (auto, translucent, opaque).</summary>
    public static readonly IReadOnlyList<string> MaterialModes = ["auto", "translucent", "opaque"];

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
        new("color.health.healthy.background", TokenKind.Colour),
        new("color.health.healthy.text", TokenKind.Colour),
        new("color.health.degraded.background", TokenKind.Colour),
        new("color.health.degraded.text", TokenKind.Colour),
        new("color.health.failing.background", TokenKind.Colour),
        new("color.health.failing.text", TokenKind.Colour),
        new("color.health.recovering.background", TokenKind.Colour),
        new("color.health.recovering.text", TokenKind.Colour),
        new("color.health.frozen.background", TokenKind.Colour),
        new("color.health.frozen.text", TokenKind.Colour),
        new("color.health.unobserved.background", TokenKind.Colour),
        new("color.health.unobserved.text", TokenKind.Colour),
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
        .. HueSpecs(),
        new("color.hue.lightness", TokenKind.Number, 2, 0.2, 0.95),
        new("color.hue.chroma", TokenKind.Number, 2, 0.0, 0.4),
        new("color.hue.origin", TokenKind.Number, 2, 0.0, 360.0),
        new("material.mode", TokenKind.Choice, 2, Choices: MaterialModes),
        new("material.tint", TokenKind.Colour, 2),
        new("material.opacity", TokenKind.Number, 2, 0.0, 1.0),
        new("material.fallback.chrome", TokenKind.Colour, 2),
        new("material.fallback.sheet", TokenKind.Colour, 2),
        new("motion.duration.hover", TokenKind.Duration, 2),
        new("motion.duration.palette", TokenKind.Duration, 2),
        new("motion.duration.tab", TokenKind.Duration, 2),
        new("motion.duration.message", TokenKind.Duration, 2),
        new("motion.duration.sheet", TokenKind.Duration, 2),
        new("motion.duration.swipe", TokenKind.Duration, 2),
        new("motion.duration.reduced", TokenKind.Duration, 2),
        new("motion.spring.stiffness", TokenKind.Number, 2, 50.0, 1000.0),
        new("motion.spring.damping", TokenKind.Number, 2, 0.5, 1.2),
        new("motion.scale.desktop", TokenKind.Number, 2, 0.0, 1.0),
        new("motion.liquid.squash.max", TokenKind.Number, 2, 0.0, 0.5),
        new("motion.liquid.stretch.max", TokenKind.Number, 2, 0.0, 0.5),
        new("motion.liquid.ripple.size", TokenKind.Length, 2),
        new("motion.liquid.ripple.duration", TokenKind.Duration, 2),
        new("motion.liquid.merge.softness", TokenKind.Number, 2, 0.0, 1.0),
    ];

    private static IEnumerable<TokenSpec> HueSpecs()
    {
        for (int i = 0; i < HueCount; i++)
        {
            yield return new TokenSpec(HueToken(i), TokenKind.Colour, 2);
        }
    }

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
        new("color.health.healthy.text", "color.health.healthy.background"),
        new("color.health.degraded.text", "color.health.degraded.background"),
        new("color.health.failing.text", "color.health.failing.background"),
        new("color.health.recovering.text", "color.health.recovering.background"),
        new("color.health.frozen.text", "color.health.frozen.background"),
        new("color.health.unobserved.text", "color.health.unobserved.background"),
        new("color.syntax.keyword", "color.background.editor"),
        new("color.syntax.string", "color.background.editor"),
        new("color.syntax.comment", "color.background.editor"),
        new("color.syntax.number", "color.background.editor"),
        new("color.syntax.type", "color.background.editor"),
        new("color.syntax.default", "color.background.editor"),
    ];
}
