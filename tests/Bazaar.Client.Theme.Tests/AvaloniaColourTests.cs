using Avalonia.Media;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Theme.Tests;

public class AvaloniaColourTests
{
    [Fact]
    public void Opaque_colour_is_emitted_unchanged()
    {
        string xaml = Generate(withBorder: "#45475A");

        Assert.Contains("<Color x:Key=\"Bazaar.color.border.default\">#45475A</Color>", xaml, StringComparison.Ordinal);
        Assert.Equal(Color.Parse("#45475A"), Color.Parse(Emitted(xaml, "color.border.default")));
    }

    [Fact]
    public void Translucent_colour_moves_alpha_to_the_front()
    {
        string xaml = Generate(withBorder: "#45475A80");

        Assert.Contains("<Color x:Key=\"Bazaar.color.border.default\">#8045475A</Color>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Translucent_colour_parses_in_Avalonia_with_the_theme_alpha()
    {
        string xaml = Generate(withBorder: "#45475A80");

        Color parsed = Color.Parse(Emitted(xaml, "color.border.default"));

        Assert.Equal(0x80, parsed.A);
        Assert.Equal(0x45, parsed.R);
        Assert.Equal(0x47, parsed.G);
        Assert.Equal(0x5A, parsed.B);
    }

    [Fact]
    public void Brush_for_translucent_colour_still_references_the_fixed_colour()
    {
        string xaml = Generate(withBorder: "#45475A80");

        Assert.Contains(
            "<SolidColorBrush x:Key=\"Bazaar.brush.color.border.default\" Color=\"{StaticResource Bazaar.color.border.default}\" />",
            xaml,
            StringComparison.Ordinal);
    }

    private static string Generate(string withBorder)
    {
        ThemeDefinition dark = LoadDark();
        var tokens = new Dictionary<string, string>(dark.Tokens, StringComparer.Ordinal)
        {
            ["color.border.default"] = withBorder,
        };
        return AvaloniaThemeGenerator.Generate(dark with { Tokens = tokens });
    }

    private static string Emitted(string xaml, string token)
    {
        string prefix = "<Color x:Key=\"Bazaar." + token + "\">";
        int start = xaml.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = xaml.IndexOf("</Color>", start, StringComparison.Ordinal);
        return xaml[start..end];
    }

    private static ThemeDefinition LoadDark()
    {
        ThemeLoadResult result = ThemeValidator.Validate(ThemeFixtures.Read("dark.theme.json"));
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}")));
        return result.Definition!;
    }
}
