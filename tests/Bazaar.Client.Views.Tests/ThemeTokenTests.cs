using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Client.Views.Tests.Fixtures;

namespace Bazaar.Client.Views.Tests;

/// <summary>Views name theme tokens; the mapping resolves them from the theme file, never from raw values.</summary>
public class ThemeTokenTests
{
    [AvaloniaFact]
    public void Body_text_resolves_font_size_family_and_primary_colour_from_tokens()
    {
        var harness = new Harness();

        var text = Assert.IsType<TextBlock>(harness.Build(ViewFixtures.Minimal("text")));

        Assert.Equal(14, text.FontSize);
        Assert.Equal("Inter", text.FontFamily.Name);
        Assert.Equal(ViewFixtures.Parse(ViewFixtures.ColourText), ViewFixtures.ColorOf(text.Foreground));
    }

    [AvaloniaFact]
    public void Eight_digit_theme_colours_read_alpha_last_as_the_theme_file_writes_them()
    {
        var harness = new Harness();
        var theme = ViewFixtures.Theme();
        var tokens = new Dictionary<string, string>(theme.Tokens, StringComparer.Ordinal) { ["color.text.primary"] = "#11223380" };
        var node = ViewFixtures.Node("text", [], ("text", ViewFixtures.Lit("x")));

        var text = Assert.IsType<TextBlock>(harness.Build(ViewFixtures.Document(node), theme with { Tokens = tokens }));

        // #RRGGBBAA: red 0x11, green 0x22, blue 0x33, alpha 0x80.
        Assert.Equal(Color.FromArgb(0x80, 0x11, 0x22, 0x33), ViewFixtures.ColorOf(text.Foreground));
    }

    [AvaloniaFact]
    public void Layout_gap_resolves_the_space_token_in_pixels()
    {
        var harness = new Harness();

        var stack = Assert.IsType<StackPanel>(harness.Build(ViewFixtures.Minimal("stack")));

        Assert.Equal(8, stack.Spacing);
    }

    [AvaloniaFact]
    public void Badge_neutral_background_resolves_from_the_surface_token()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("badge", [], ("text", ViewFixtures.Lit("3")));

        var neutral = Assert.IsType<Border>(harness.Build(ViewFixtures.Document(node)));

        Assert.Equal(ViewFixtures.Parse(ViewFixtures.ColourSurface), ViewFixtures.ColorOf(neutral.Background));
    }

    [AvaloniaFact]
    public void A_theme_without_a_token_the_view_uses_fails_closed_with_the_token_named()
    {
        var harness = new Harness();

        Control control = harness.Build(ViewFixtures.Minimal("text"), ViewFixtures.Theme(includeTextPrimary: false));

        Assert.Contains("color.text.primary", ViewFixtures.PlaceholderText(control));
    }

    [AvaloniaFact]
    public void Unknown_text_style_is_not_a_token_and_fails_closed()
    {
        var harness = new Harness();
        var node = ViewFixtures.Node("text", [], ("text", ViewFixtures.Lit("x")), ("style", ViewFixtures.Lit("huge")));

        Control control = harness.Build(ViewFixtures.Document(node));

        Assert.Contains("not a style token", ViewFixtures.PlaceholderText(control));
    }
}
