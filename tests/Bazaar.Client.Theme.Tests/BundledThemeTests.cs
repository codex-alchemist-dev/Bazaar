using System.Text.Json.Nodes;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Theme.Tests;

public class BundledThemeTests
{
    public static TheoryData<string> Themes => new(BundledThemes.Names);

    [Fact]
    public void Void_is_the_default_and_all_three_ship()
    {
        Assert.Equal("void", BundledThemes.Default);
        Assert.Equal(["void", "graphite", "paper"], BundledThemes.Names);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Bundled_theme_passes_the_validator_as_schema_version_2(string name)
    {
        ThemeLoadResult result = ThemeValidator.Validate(BundledThemes.ReadJson(name));

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code} {i.Message}")));
        Assert.Equal(2, result.Definition!.SchemaVersion);
        Assert.Equal(ThemeTokens.Required.Count, result.Definition.Tokens.Count);
        Assert.Equal("auto", result.Definition.Tokens["material.mode"]);
    }

    [Fact]
    public void Void_has_a_pure_black_ground_and_paper_is_light()
    {
        Assert.Equal("#000000", BundledThemes.Load("void").Tokens["color.background.base"]);
        Assert.Equal(ThemeBase.Dark, BundledThemes.Load("void").Base);
        Assert.Equal(ThemeBase.Dark, BundledThemes.Load("graphite").Base);
        Assert.Equal(ThemeBase.Light, BundledThemes.Load("paper").Base);
    }

    [Fact]
    public void Unknown_bundled_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BundledThemes.ReadJson("../void"));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Avalonia_output_matches_golden(string name)
    {
        GoldenFile.Check(name + ".avalonia.txt", AvaloniaThemeGenerator.Generate(BundledThemes.Load(name)));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Monaco_output_matches_golden(string name)
    {
        GoldenFile.Check(name + ".monaco.json", MonacoThemeGenerator.Generate(BundledThemes.Load(name)));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Avalonia_output_carries_every_v2_token_kind(string name)
    {
        string xaml = AvaloniaThemeGenerator.Generate(BundledThemes.Load(name));

        Assert.Contains("<Color x:Key=\"Bazaar.color.hue.11\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<x:String x:Key=\"Bazaar.material.mode\">auto</x:String>", xaml, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"Bazaar.motion.duration.sheet\">420</x:Double>", xaml, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"Bazaar.motion.spring.damping\">0.86</x:Double>", xaml, StringComparison.Ordinal);
        Assert.Contains("schemaVersion 2", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Monaco_output_is_valid_json_with_editor_colours_from_the_theme(string name)
    {
        ThemeDefinition theme = BundledThemes.Load(name);

        JsonObject root = JsonNode.Parse(MonacoThemeGenerator.Generate(theme))!.AsObject();

        Assert.Equal(theme.Base == ThemeBase.Dark ? "vs-dark" : "vs", root["base"]!.GetValue<string>());
        Assert.Equal(theme.Tokens["color.background.editor"], root["colors"]!["editor.background"]!.GetValue<string>());
        Assert.Equal(6, root["rules"]!.AsArray().Count);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Body_and_accent_text_pass_on_every_surface_including_the_translucent_worst_case(string name)
    {
        ThemeDefinition theme = BundledThemes.Load(name);
        string tint = theme.Tokens["material.tint"];
        double opacity = double.Parse(theme.Tokens["material.opacity"], System.Globalization.CultureInfo.InvariantCulture);

        foreach (string text in ThemeTokens.MaterialTextTokens)
        {
            foreach (string backdrop in new[] { "#000000", "#FFFFFF" })
            {
                string composite = ThemeValidator.Composite(tint, opacity, backdrop);
                Assert.True(ThemeValidator.ContrastRatio(theme.Tokens[text], composite) >= ThemeTokens.MinimumContrastRatio, $"{name} {text} over {backdrop}");
            }

            Assert.True(ThemeValidator.ContrastRatio(theme.Tokens[text], theme.Tokens["material.fallback.chrome"]) >= ThemeTokens.MinimumContrastRatio);
            Assert.True(ThemeValidator.ContrastRatio(theme.Tokens[text], theme.Tokens["material.fallback.sheet"]) >= ThemeTokens.MinimumContrastRatio);
        }
    }
}
