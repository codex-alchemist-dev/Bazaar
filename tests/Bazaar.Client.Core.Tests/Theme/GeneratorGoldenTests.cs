using System.Text.Json.Nodes;
using Bazaar.Client.Core.Theme;

namespace Bazaar.Client.Core.Tests.Theme;

public class GeneratorGoldenTests
{
    [Theory]
    [InlineData("dark.theme.json", "dark.avalonia.txt")]
    [InlineData("light.theme.json", "light.avalonia.txt")]
    public void Avalonia_output_matches_golden(string fixture, string golden)
    {
        ThemeDefinition theme = LoadValid(fixture);

        GoldenFile.Check(golden, AvaloniaThemeGenerator.Generate(theme));
    }

    [Theory]
    [InlineData("dark.theme.json", "dark.monaco.json")]
    [InlineData("light.theme.json", "light.monaco.json")]
    public void Monaco_output_matches_golden(string fixture, string golden)
    {
        ThemeDefinition theme = LoadValid(fixture);

        GoldenFile.Check(golden, MonacoThemeGenerator.Generate(theme));
    }

    [Fact]
    public void Monaco_output_is_pure_data_with_expected_shape()
    {
        ThemeDefinition theme = LoadValid("dark.theme.json");

        JsonObject root = JsonNode.Parse(MonacoThemeGenerator.Generate(theme))!.AsObject();

        Assert.Equal("vs-dark", root["base"]!.GetValue<string>());
        Assert.True(root["inherit"]!.GetValue<bool>());
        JsonArray rules = root["rules"]!.AsArray();
        Assert.Equal(6, rules.Count);
        Assert.Equal("keyword", rules[1]!["token"]!.GetValue<string>());
        Assert.Equal("CBA6F7", rules[1]!["foreground"]!.GetValue<string>());
        Assert.Equal("#181825", root["colors"]!["editor.background"]!.GetValue<string>());
    }

    [Fact]
    public void Avalonia_output_contains_no_urls_or_remote_fonts()
    {
        string xaml = AvaloniaThemeGenerator.Generate(LoadValid("dark.theme.json"))
            .Replace("https://github.com/avaloniaui", "", StringComparison.Ordinal)
            .Replace("http://schemas.microsoft.com/winfx/2006/xaml", "", StringComparison.Ordinal);

        // Only the two XAML namespace declarations may carry a scheme.
        Assert.DoesNotContain("://", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", xaml, StringComparison.Ordinal);
    }

    private static ThemeDefinition LoadValid(string fixture)
    {
        ThemeLoadResult result = ThemeValidator.Validate(ThemeFixtures.Read(fixture));
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}")));
        return result.Definition!;
    }
}
