using System.Globalization;

namespace Bazaar.Client.Theme;

/// <summary>
/// The shared hue family (docs/design/93-client-visual-system-and-motion.md, Colour). Hues step by the golden angle
/// (137.508 degrees) in OKLCH at a fixed lightness and chroma per theme, so any number of items get distinct colours and a
/// pane and the Constellation render the same hue for the same item. Pure: no Avalonia types and no I/O.
/// </summary>
public static class ThemeHues
{
    /// <summary>The golden angle in degrees, 360 times (1 minus 1/phi).</summary>
    public const double GoldenAngleDegrees = 137.508;

    /// <summary>Default OKLCH lightness for dark themes (Void, Graphite): brighter stops.</summary>
    public const double DarkLightness = 0.80;

    /// <summary>Default OKLCH lightness for light themes (Paper): deeper stops of the same hues.</summary>
    public const double LightLightness = 0.52;

    /// <summary>Default OKLCH chroma. Colours outside sRGB at this lightness lose chroma, never lightness.</summary>
    public const double DefaultChroma = 0.15;

    /// <summary>Default angle of hue 0, in degrees.</summary>
    public const double DefaultOrigin = 25.0;

    /// <summary>
    /// The colour for item <paramref name="index"/> in a theme as #RRGGBB. Indexes 0 to 11 return the theme's explicit
    /// color.hue.N tokens (so a Rubedo override of a stop shows everywhere); higher indexes continue the golden-angle sequence
    /// from the theme's color.hue.lightness, color.hue.chroma and color.hue.origin.
    /// </summary>
    public static string HueFamily(ThemeDefinition theme, int index)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        if (index < ThemeTokens.HueCount)
        {
            return theme.Tokens[ThemeTokens.HueToken(index)];
        }

        return Generate(
            Number(theme, "color.hue.lightness"),
            Number(theme, "color.hue.chroma"),
            Number(theme, "color.hue.origin"),
            index);
    }

    /// <summary>The generated golden-angle colour for <paramref name="index"/> from explicit parameters, as #RRGGBB.</summary>
    public static string Generate(double lightness, double chroma, double originDegrees, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        double hue = (originDegrees + (index * GoldenAngleDegrees)) % 360.0;
        return FromOklch(lightness, chroma, hue);
    }

    /// <summary>
    /// An OKLCH colour as #RRGGBB. If it is outside the sRGB gamut, chroma is reduced (lightness and hue are kept) until it fits.
    /// </summary>
    public static string FromOklch(double lightness, double chroma, double hueDegrees)
    {
        double hue = hueDegrees * Math.PI / 180.0;
        double low = 0.0;
        double high = chroma;
        (double R, double G, double B) rgb = ToLinear(lightness, high, hue);
        if (!InGamut(rgb))
        {
            for (int i = 0; i < 24; i++)
            {
                double mid = (low + high) / 2.0;
                if (InGamut(ToLinear(lightness, mid, hue)))
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            rgb = ToLinear(lightness, low, hue);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{Encode(rgb.R):X2}{Encode(rgb.G):X2}{Encode(rgb.B):X2}");
    }

    /// <summary>
    /// The lightness nearest to the polarity's default at which all 12 explicit hues (and the whole golden sequence) reach 4.5:1
    /// on every surface, or the default if none does. Used to migrate a schemaVersion 1 theme.
    /// </summary>
    public static double FindLightness(ThemeBase polarity, IReadOnlyList<string> surfaces, double chroma, double origin)
    {
        double start = polarity == ThemeBase.Dark ? DarkLightness : LightLightness;
        double step = polarity == ThemeBase.Dark ? 0.01 : -0.01;
        for (double lightness = start; lightness is >= 0.2 and <= 0.95; lightness += step)
        {
            if (PassesAll(lightness, chroma, origin, surfaces))
            {
                return Math.Round(lightness, 3);
            }
        }

        return start;
    }

    private static bool PassesAll(double lightness, double chroma, double origin, IReadOnlyList<string> surfaces)
    {
        for (int i = 0; i < ThemeTokens.HueCount; i++)
        {
            string hue = Generate(lightness, chroma, origin, i);
            foreach (string surface in surfaces)
            {
                if (ThemeValidator.ContrastRatio(hue, surface) < ThemeTokens.MinimumContrastRatio)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static double Number(ThemeDefinition theme, string token) =>
        double.Parse(theme.Tokens[token], CultureInfo.InvariantCulture);

    // Bjorn Ottosson's OKLab to linear sRGB.
    private static (double R, double G, double B) ToLinear(double lightness, double chroma, double hue)
    {
        double a = chroma * Math.Cos(hue);
        double b = chroma * Math.Sin(hue);
        double l = Cube(lightness + (0.3963377774 * a) + (0.2158037573 * b));
        double m = Cube(lightness - (0.1055613458 * a) - (0.0638541728 * b));
        double s = Cube(lightness - (0.0894841775 * a) - (1.2914855480 * b));
        return (
            (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
            (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
            (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s));
    }

    private static double Cube(double x) => x * x * x;

    private static bool InGamut((double R, double G, double B) c) =>
        c.R is >= -0.0005 and <= 1.0005 && c.G is >= -0.0005 and <= 1.0005 && c.B is >= -0.0005 and <= 1.0005;

    private static int Encode(double linear)
    {
        double clamped = Math.Clamp(linear, 0.0, 1.0);
        double srgb = clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (int)Math.Round(srgb * 255.0, MidpointRounding.AwayFromZero);
    }
}
