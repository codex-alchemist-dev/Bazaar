using System.Globalization;
using Avalonia.Media;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Materials.Avalonia;

/// <summary>Builds the brush for a surface from the theme's material.* tokens and a policy decision.</summary>
public static class MaterialBrushes
{
    /// <summary>
    /// The brush for <paramref name="surface"/> under <paramref name="decision"/>. A translucent kind gets material.tint at
    /// material.opacity; opaque gets the theme's fallback (material.fallback.chrome or .sheet) or, for content surfaces,
    /// the theme's own background colour.
    /// </summary>
    public static IBrush For(ThemeDefinition theme, MaterialSurface surface, MaterialDecision decision)
    {
        if (decision.IsOpaque)
        {
            return new SolidColorBrush(ParseColour(theme.Tokens[OpaqueToken(surface)]));
        }

        Color tint = ParseColour(theme.Tokens["material.tint"]);
        double opacity = double.Parse(theme.Tokens["material.opacity"], CultureInfo.InvariantCulture);
        byte alpha = (byte)Math.Round(tint.A * Math.Clamp(opacity, 0.0, 1.0));
        return new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
    }

    internal static string OpaqueToken(MaterialSurface surface) => surface switch
    {
        MaterialSurface.Chrome or MaterialSurface.Strip or MaterialSurface.ToolWindow => "material.fallback.chrome",
        MaterialSurface.Sheet or MaterialSurface.Overlay => "material.fallback.sheet",
        MaterialSurface.MessageList => "color.background.base",
        MaterialSurface.Editor or MaterialSurface.Terminal or MaterialSurface.Diff => "color.background.editor",
        _ => "color.background.surface",
    };

    // Theme colours are #RRGGBB or #RRGGBBAA (alpha last); Avalonia's Color.Parse reads the 8-digit form as #AARRGGBB.
    internal static Color ParseColour(string value)
    {
        byte Hex(int i) => byte.Parse(value.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return value.Length == 9
            ? Color.FromArgb(Hex(7), Hex(1), Hex(3), Hex(5))
            : Color.FromRgb(Hex(1), Hex(3), Hex(5));
    }
}
