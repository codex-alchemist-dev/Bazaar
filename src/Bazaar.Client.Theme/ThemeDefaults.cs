using System.Globalization;

namespace Bazaar.Client.Theme;

/// <summary>
/// Defaults for the schemaVersion 2 tokens, used to migrate a schemaVersion 1 file. The defaults keep the old look: the
/// material is opaque (tint and fallbacks are the theme's own surface), so nothing becomes translucent by migration; the hue
/// family and motion tokens take the doc 93 initial values.
/// </summary>
public static class ThemeDefaults
{
    /// <summary>
    /// Fills every schemaVersion 2 token that <paramref name="tokens"/> lacks. Needs the version 1 colours to be present
    /// and valid; the caller checks that first.
    /// </summary>
    public static void Fill(Dictionary<string, string> tokens, ThemeBase polarity)
    {
        string[] surfaces = [.. ThemeTokens.HueBackgrounds.Select(name => tokens[name])];
        double chroma = ThemeHues.DefaultChroma;
        double origin = ThemeHues.DefaultOrigin;
        double lightness = ThemeHues.FindLightness(polarity, surfaces, chroma, origin);

        tokens["color.hue.lightness"] = Num(lightness);
        tokens["color.hue.chroma"] = Num(chroma);
        tokens["color.hue.origin"] = Num(origin);
        for (int i = 0; i < ThemeTokens.HueCount; i++)
        {
            tokens[ThemeTokens.HueToken(i)] = ThemeHues.Generate(lightness, chroma, origin, i);
        }

        string surface = tokens["color.background.surface"];
        tokens["material.mode"] = "opaque";
        tokens["material.tint"] = surface;
        tokens["material.opacity"] = "1";
        tokens["material.fallback.chrome"] = surface;
        tokens["material.fallback.sheet"] = surface;

        tokens["motion.duration.hover"] = "80";
        tokens["motion.duration.palette"] = "120";
        tokens["motion.duration.tab"] = "180";
        tokens["motion.duration.message"] = "320";
        tokens["motion.duration.sheet"] = "420";
        tokens["motion.duration.swipe"] = "340";
        tokens["motion.duration.reduced"] = ThemeTokens.ReducedDurationFloorMs.ToString(CultureInfo.InvariantCulture);
        tokens["motion.spring.stiffness"] = "380";
        tokens["motion.spring.damping"] = "0.86";
        tokens["motion.scale.desktop"] = "0.5";
        tokens["motion.liquid.squash.max"] = "0.12";
        tokens["motion.liquid.stretch.max"] = "0.18";
        tokens["motion.liquid.ripple.size"] = "160";
        tokens["motion.liquid.ripple.duration"] = "900";
        tokens["motion.liquid.merge.softness"] = "0.6";

        tokens["layout.sidebar.width.expanded"] = "256";
        tokens["layout.sidebar.width.collapsed"] = "48";
        tokens["layout.strip.height"] = "48";
        tokens["layout.sheet.width"] = "448";
        tokens["layout.sheet.side.width"] = "384";
        tokens["material.scrim.opacity"] = "0.4";
        tokens["gesture.sheet.dismiss.fraction"] = "0.3";
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
