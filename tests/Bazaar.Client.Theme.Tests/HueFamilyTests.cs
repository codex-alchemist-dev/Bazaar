using System.Globalization;
using System.Text;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Theme.Tests;

public class HueFamilyTests
{
    public static TheoryData<string> Themes => new(BundledThemes.Names);

    [Theory]
    [MemberData(nameof(Themes))]
    public void Hue_family_matches_golden_values(string name)
    {
        ThemeDefinition theme = BundledThemes.Load(name);
        var sb = new StringBuilder();
        for (int i = 0; i < 24; i++)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ThemeHues.HueFamily(theme, i)).Append('\n');
        }

        GoldenFile.Check(name + ".hues.txt", sb.ToString());
    }

    [Fact]
    public void Generation_steps_by_the_golden_angle()
    {
        // Same lightness and chroma, so two indexes land on the angle origin + index * 137.508.
        Assert.Equal(ThemeHues.FromOklch(0.8, 0.15, 25.0), ThemeHues.Generate(0.8, 0.15, 25.0, 0));
        Assert.Equal(ThemeHues.FromOklch(0.8, 0.15, 25.0 + 137.508), ThemeHues.Generate(0.8, 0.15, 25.0, 1));
        Assert.Equal(ThemeHues.FromOklch(0.8, 0.15, (25.0 + (2 * 137.508)) % 360.0), ThemeHues.Generate(0.8, 0.15, 25.0, 2));
        Assert.Equal(137.508, ThemeHues.GoldenAngleDegrees);
    }

    [Fact]
    public void Generation_is_deterministic_and_well_formed()
    {
        for (int i = 0; i < 200; i++)
        {
            string hue = ThemeHues.Generate(0.8, 0.15, 25.0, i);
            Assert.Equal(hue, ThemeHues.Generate(0.8, 0.15, 25.0, i));
            Assert.Matches("^#[0-9A-F]{6}$", hue);
        }
    }

    [Fact]
    public void First_hundred_hues_are_distinct()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            Assert.True(seen.Add(ThemeHues.Generate(0.8, 0.15, 25.0, i)), $"hue {i} repeated");
        }
    }

    [Theory]
    [InlineData(0.0, "#000000")]
    [InlineData(1.0, "#FFFFFF")]
    public void Zero_chroma_is_grey_at_the_ends(double lightness, string expected)
    {
        Assert.Equal(expected, ThemeHues.FromOklch(lightness, 0.0, 0.0));
    }

    [Fact]
    public void Out_of_gamut_chroma_is_reduced_not_clipped_to_another_lightness()
    {
        string vivid = ThemeHues.FromOklch(0.8, 0.4, 200.0); // far outside sRGB
        string tame = ThemeHues.FromOklch(0.8, 0.0, 200.0);

        Assert.Matches("^#[0-9A-F]{6}$", vivid);
        // Lightness stays close: relative luminance within a small band of the grey of the same OKLCH lightness.
        Assert.InRange(ThemeValidator.RelativeLuminance(vivid), ThemeValidator.RelativeLuminance(tame) * 0.6, ThemeValidator.RelativeLuminance(tame) * 1.6);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_hue_passes_text_and_graphic_contrast_on_the_theme_surfaces(string name)
    {
        ThemeDefinition theme = BundledThemes.Load(name);

        for (int i = 0; i < 1000; i++)
        {
            string hue = ThemeHues.HueFamily(theme, i);
            foreach (string background in ThemeTokens.HueBackgrounds)
            {
                double ratio = ThemeValidator.ContrastRatio(hue, theme.Tokens[background]);
                Assert.True(ratio >= ThemeTokens.MinimumContrastRatio, $"{name} hue {i} {hue} on {background} is {ratio:F2}:1");
                Assert.True(ratio >= ThemeTokens.MinimumGraphicContrastRatio);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Explicit_stops_equal_the_generated_sequence(string name)
    {
        ThemeDefinition theme = BundledThemes.Load(name);
        double lightness = double.Parse(theme.Tokens["color.hue.lightness"], CultureInfo.InvariantCulture);
        double chroma = double.Parse(theme.Tokens["color.hue.chroma"], CultureInfo.InvariantCulture);
        double origin = double.Parse(theme.Tokens["color.hue.origin"], CultureInfo.InvariantCulture);

        for (int i = 0; i < ThemeTokens.HueCount; i++)
        {
            Assert.Equal(ThemeHues.Generate(lightness, chroma, origin, i), theme.Tokens[ThemeTokens.HueToken(i)]);
        }
    }

    [Fact]
    public void Dark_themes_use_brighter_stops_than_paper()
    {
        double void_ = Lightness("void");
        double graphite = Lightness("graphite");
        double paper = Lightness("paper");

        Assert.True(void_ > paper && graphite > paper);
        Assert.Equal(void_, graphite); // Void and Graphite share the bright stops
    }

    [Fact]
    public void An_overridden_stop_is_what_every_caller_sees_and_later_indexes_continue_the_sequence()
    {
        ThemeDefinition original = BundledThemes.Load("void");
        var tokens = new Dictionary<string, string>(original.Tokens) { ["color.hue.3"] = "#112233" };
        var overridden = original with { Tokens = tokens };

        Assert.Equal("#112233", ThemeHues.HueFamily(overridden, 3));
        Assert.Equal(ThemeHues.HueFamily(original, 12), ThemeHues.HueFamily(overridden, 12));
    }

    [Fact]
    public void Negative_index_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThemeHues.HueFamily(BundledThemes.Load("void"), -1));
    }

    private static double Lightness(string name) =>
        double.Parse(BundledThemes.Load(name).Tokens["color.hue.lightness"], CultureInfo.InvariantCulture);
}
