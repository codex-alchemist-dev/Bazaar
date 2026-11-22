using Bazaar.Client.Theme;

namespace Bazaar.Client.Views;

/// <summary>The theme a head uses until the operator or user supplies one (doc 41 section 5). It passes the same validator as any theme file.</summary>
public static class BuiltInThemes
{
    private const string _darkJson = """
        {
          "schemaVersion": 1,
          "name": "bazaar-dark",
          "base": "dark",
          "tokens": {
            "color.background.base": "#0B0E14",
            "color.background.surface": "#141A24",
            "color.background.editor": "#0F141C",
            "color.text.primary": "#E6EAF2",
            "color.text.secondary": "#A9B3C5",
            "color.text.accent": "#8AB4FF",
            "color.border.default": "#2A3344",
            "color.accent.default": "#4C8DFF",
            "color.badge.owner.background": "#5B3A12",
            "color.badge.owner.text": "#FFD9A0",
            "color.badge.agent.background": "#16385C",
            "color.badge.agent.text": "#BFE0FF",
            "color.badge.system.background": "#2B2F3A",
            "color.badge.system.text": "#D5DAE5",
            "color.health.healthy.background": "#4ADBA1",
            "color.health.healthy.text": "#12151B",
            "color.health.degraded.background": "#F3B037",
            "color.health.degraded.text": "#12151B",
            "color.health.failing.background": "#FFA098",
            "color.health.failing.text": "#12151B",
            "color.health.recovering.background": "#FFA47A",
            "color.health.recovering.text": "#12151B",
            "color.health.frozen.background": "#A1BCFF",
            "color.health.frozen.text": "#12151B",
            "color.health.unobserved.background": "#3A4252",
            "color.health.unobserved.text": "#E6E9EF",
            "color.syntax.keyword": "#C792EA",
            "color.syntax.string": "#A5E075",
            "color.syntax.comment": "#8A94A8",
            "color.syntax.number": "#F6B26B",
            "color.syntax.type": "#7FD6E8",
            "color.syntax.default": "#E6EAF2",
            "font.family.ui": "Inter",
            "font.family.mono": "Cascadia Mono",
            "font.size.body": 14,
            "font.size.small": 12,
            "space.xs": 4,
            "space.sm": 8,
            "space.md": 12,
            "space.lg": 16,
            "radius.sm": 4,
            "radius.md": 8,
            "reducedMotion": false
          }
        }
        """;

    /// <summary>The built-in dark theme.</summary>
    public static ThemeDefinition Dark { get; } = ThemeValidator.Validate(_darkJson).Definition
        ?? throw new InvalidOperationException("The built-in dark theme is invalid: " + string.Join("; ", ThemeValidator.Validate(_darkJson).Issues.Select(i => i.Path + " " + i.Code)));
}
