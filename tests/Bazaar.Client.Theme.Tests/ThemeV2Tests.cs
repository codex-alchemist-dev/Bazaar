using System.Text.Json.Nodes;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Theme.Tests;

/// <summary>schemaVersion 2 (docs/design/93, Theme file additions): new tokens, v1 migration, translucent-surface contrast.</summary>
public class ThemeV2Tests
{
    [Fact]
    public void V1_file_migrates_to_v2_with_defaults_and_keeps_its_own_values()
    {
        ThemeLoadResult result = ThemeValidator.Validate(ThemeFixtures.Read("dark.theme.json"));

        Assert.True(result.IsValid, Describe(result));
        ThemeDefinition theme = result.Definition!;
        Assert.Equal(2, theme.SchemaVersion);
        Assert.Equal("#1E1E2E", theme.Tokens["color.background.base"]); // v1 token untouched
        Assert.Equal("opaque", theme.Tokens["material.mode"]); // migration never makes a theme translucent
        Assert.Equal("1", theme.Tokens["material.opacity"]);
        Assert.Equal("#282839", theme.Tokens["material.fallback.chrome"]);
        Assert.Equal("320", theme.Tokens["motion.duration.message"]);
        Assert.Equal("90", theme.Tokens["motion.duration.reduced"]);
        Assert.Equal("0.5", theme.Tokens["motion.scale.desktop"]);
        Assert.Equal(ThemeTokens.Required.Count, theme.Tokens.Count);
    }

    [Fact]
    public void Migrated_hues_are_the_generated_family_and_pass_contrast()
    {
        ThemeDefinition dark = ThemeValidator.Validate(ThemeFixtures.Read("dark.theme.json")).Definition!;
        ThemeDefinition light = ThemeValidator.Validate(ThemeFixtures.Read("light.theme.json")).Definition!;

        foreach (ThemeDefinition theme in new[] { dark, light })
        {
            for (int i = 0; i < ThemeTokens.HueCount; i++)
            {
                string hue = theme.Tokens[ThemeTokens.HueToken(i)];
                Assert.Equal(ThemeHues.HueFamily(theme, i), hue);
                foreach (string background in ThemeTokens.HueBackgrounds)
                {
                    Assert.True(ThemeValidator.ContrastRatio(hue, theme.Tokens[background]) >= ThemeTokens.MinimumContrastRatio);
                }
            }
        }

        // Dark themes get brighter stops than light ones.
        Assert.True(double.Parse(dark.Tokens["color.hue.lightness"], System.Globalization.CultureInfo.InvariantCulture)
            > double.Parse(light.Tokens["color.hue.lightness"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void V1_file_may_not_carry_v2_tokens()
    {
        string json = MutateV1(root => Tokens(root)["material.mode"] = "auto");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/material.mode" && i.Code == "unknown-token");
    }

    [Fact]
    public void Unknown_token_is_still_rejected_in_v2()
    {
        string json = MutateV2("void", root => Tokens(root)["material.blur"] = 12);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/material.blur" && i.Code == "unknown-token");
    }

    [Fact]
    public void V2_file_must_hold_every_v2_token()
    {
        string json = MutateV2("void", root => Tokens(root).Remove("motion.spring.damping"));

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/motion.spring.damping" && i.Code == "missing-token");
    }

    [Theory]
    [InlineData("material.mode", "\"frosted\"")]
    [InlineData("material.opacity", "1.5")]
    [InlineData("material.opacity", "\"0.5\"")]
    [InlineData("motion.duration.sheet", "-1")]
    [InlineData("motion.duration.sheet", "9000")]
    [InlineData("motion.duration.sheet", "420.5")]
    [InlineData("motion.spring.stiffness", "5")]
    [InlineData("motion.scale.desktop", "2")]
    [InlineData("motion.liquid.merge.softness", "-0.1")]
    [InlineData("color.hue.origin", "361")]
    [InlineData("color.hue.3", "\"blue\"")]
    public void Bad_v2_values_are_rejected(string token, string value)
    {
        string json = MutateV2("void", root => Tokens(root)[token] = JsonNode.Parse(value));

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/" + token && i.Code == "bad-value");
    }

    [Fact]
    public void Number_and_duration_values_are_canonicalised()
    {
        string json = MutateV2("void", root =>
        {
            Tokens(root)["motion.spring.damping"] = 0.8600001;
            Tokens(root)["motion.duration.sheet"] = 400;
        });

        ThemeDefinition theme = ThemeValidator.Validate(json).Definition!;

        Assert.Equal("0.86", theme.Tokens["motion.spring.damping"]);
        Assert.Equal("400", theme.Tokens["motion.duration.sheet"]);
    }

    [Fact]
    public void Reduced_motion_duration_cannot_exceed_the_floor()
    {
        ThemeLoadResult longer = ThemeValidator.Validate(MutateV2("void", root => Tokens(root)["motion.duration.reduced"] = 91));
        ThemeLoadResult shorter = ThemeValidator.Validate(MutateV2("void", root => Tokens(root)["motion.duration.reduced"] = 0));

        Assert.Contains(longer.Issues, i => i.Path == "/tokens/motion.duration.reduced" && i.Code == "motion-floor");
        Assert.True(shorter.IsValid);
    }

    [Fact]
    public void Hue_below_text_contrast_on_a_surface_is_rejected()
    {
        // #444444 on the black Void ground is about 2:1.
        string json = MutateV2("void", root => Tokens(root)["color.hue.4"] = "#444444");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/color.hue.4" && i.Code == "contrast");
    }

    [Fact]
    public void Translucent_surface_is_checked_against_the_brightest_backdrop()
    {
        // Void tint at 40 percent over a white wallpaper is light grey: the light text on it fails.
        string json = MutateV2("void", root => Tokens(root)["material.opacity"] = 0.4);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "material-contrast" && i.Message.Contains("#FFFFFF", StringComparison.Ordinal));
    }

    [Fact]
    public void Translucent_surface_is_checked_against_the_darkest_backdrop()
    {
        // Paper's dark text on a mostly transparent light tint over a black wallpaper fails.
        string json = MutateV2("paper", root => Tokens(root)["material.opacity"] = 0.4);

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Code == "material-contrast" && i.Message.Contains("#000000", StringComparison.Ordinal));
    }

    [Fact]
    public void Opaque_mode_skips_the_backdrop_check_but_not_the_fallback_check()
    {
        string opaque = MutateV2("void", root =>
        {
            Tokens(root)["material.mode"] = "opaque";
            Tokens(root)["material.opacity"] = 0.4;
        });
        string badFallback = MutateV2("void", root => Tokens(root)["material.fallback.chrome"] = "#7A7A7A");

        Assert.True(ThemeValidator.Validate(opaque).IsValid);
        Assert.Contains(
            ThemeValidator.Validate(badFallback).Issues,
            i => i.Path == "/tokens/material.fallback.chrome" && i.Code == "fallback-contrast");
    }

    [Fact]
    public void Translucent_tint_must_be_opaque_rgb()
    {
        string json = MutateV2("void", root => Tokens(root)["material.tint"] = "#0B0D1280");

        ThemeLoadResult result = ThemeValidator.Validate(json);

        Assert.Contains(result.Issues, i => i.Path == "/tokens/material.tint" && i.Code == "contrast-alpha");
    }

    [Fact]
    public void Composite_blends_the_tint_over_the_backdrop()
    {
        Assert.Equal("#808080", ThemeValidator.Composite("#000000", 0.5, "#FFFFFF"));
        Assert.Equal("#FFFFFF", ThemeValidator.Composite("#FFFFFF", 1.0, "#000000"));
        Assert.Equal("#000000", ThemeValidator.Composite("#FFFFFF", 0.0, "#000000"));
    }

    private static string Describe(ThemeLoadResult result) =>
        string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code} {i.Message}"));

    private static string MutateV1(Action<JsonObject> change)
    {
        JsonObject root = JsonNode.Parse(ThemeFixtures.Read("dark.theme.json"))!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static string MutateV2(string bundled, Action<JsonObject> change)
    {
        JsonObject root = JsonNode.Parse(BundledThemes.ReadJson(bundled))!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static JsonObject Tokens(JsonObject root) => root["tokens"]!.AsObject();
}
