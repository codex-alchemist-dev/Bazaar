using Bazaar.Client.Core.Theme;

namespace Bazaar.Client.Core.Tests.Theme;

public class ContrastTests
{
    [Fact]
    public void Black_on_white_is_21_to_1()
    {
        Assert.Equal(21.0, ThemeValidator.ContrastRatio("#000000", "#FFFFFF"), 6);
    }

    [Fact]
    public void Identical_colours_are_1_to_1()
    {
        Assert.Equal(1.0, ThemeValidator.ContrastRatio("#336699", "#336699"), 6);
    }

    [Fact]
    public void Grey_767676_passes_and_777777_fails_on_white()
    {
        // #767676 is the lightest grey that reaches 4.5:1 on white (WCAG reference value).
        Assert.True(ThemeValidator.ContrastRatio("#767676", "#FFFFFF") >= ThemeTokens.MinimumContrastRatio);
        Assert.True(ThemeValidator.ContrastRatio("#777777", "#FFFFFF") < ThemeTokens.MinimumContrastRatio);
    }

    [Fact]
    public void Contrast_is_symmetric()
    {
        Assert.Equal(
            ThemeValidator.ContrastRatio("#1E1E2E", "#CDD6F4"),
            ThemeValidator.ContrastRatio("#CDD6F4", "#1E1E2E"),
            9);
    }
}
