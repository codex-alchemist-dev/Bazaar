using Bazaar.Localisation;

namespace Bazaar.Localisation.Tests;

public class TextFoldTests
{
    [Fact]
    public void Precomposed_and_combining_accents_fold_to_the_same_text()
    {
        string precomposed = "Café";
        string combining = "Café";

        Assert.Equal(TextFold.Fold(precomposed), TextFold.Fold(combining));
        Assert.Equal("cafe", TextFold.Fold(precomposed));
    }

    [Theory]
    [InlineData("CAFÉ", "cafe")]
    [InlineData("Ünïcödé", "unicode")]
    [InlineData("Ångström", "angstrom")]
    [InlineData("Привет", "привет")]
    [InlineData("", "")]
    public void Folds_case_and_diacritics(string input, string expected) =>
        Assert.Equal(expected, TextFold.Fold(input));

    [Fact]
    public void Null_folds_to_empty() => Assert.Equal(string.Empty, TextFold.Fold(null));

    [Fact]
    public void Prefix_and_contains_ignore_case_and_diacritics()
    {
        Assert.True(TextFold.StartsWith("Réglages", "regl"));
        Assert.True(TextFold.Contains("Réglages", "GLAG"));
        Assert.False(TextFold.StartsWith("Réglages", "lag"));
    }

    [Fact]
    public void Unaccented_letters_that_are_not_marks_are_kept()
    {
        Assert.NotEqual(TextFold.Fold("o"), TextFold.Fold("ø"));
    }

    [Fact]
    public void Japanese_voiced_marks_are_kept()
    {
        Assert.NotEqual(TextFold.Fold("\u306F"), TextFold.Fold("\u3070"));
        Assert.NotEqual(TextFold.Fold("\u3070"), TextFold.Fold("\u3071"));
    }

    [Fact]
    public void Final_sigma_matches_capital_sigma() =>
        Assert.Equal(TextFold.Fold("\u039F\u0394\u039F\u03A3"), TextFold.Fold("\u03BF\u03B4\u03BF\u03C2"));
}
