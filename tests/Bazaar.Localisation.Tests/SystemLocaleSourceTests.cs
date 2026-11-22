namespace Bazaar.Localisation.Tests;

public class SystemLocaleSourceTests
{
    [Theory]
    [InlineData("", "en")]
    [InlineData("  ", "en")]
    [InlineData("ar-EG", "ar-EG")]
    public void The_starting_locale_is_normalised(string given, string expected) =>
        Assert.Equal(expected, new SystemLocaleSource(given).Locale);

    [Fact]
    public void Set_raises_once_per_real_change()
    {
        var source = new SystemLocaleSource("en");
        var raised = 0;
        source.LocaleChanged += (_, _) => raised++;

        source.Set("en");
        source.Set("he");
        source.Set("he");

        Assert.Equal("he", source.Locale);
        Assert.Equal(1, raised);
    }
}
