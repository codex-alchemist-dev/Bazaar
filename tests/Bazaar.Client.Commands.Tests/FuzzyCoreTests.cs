using Bazaar.Client.Commands.Fuzzy;

namespace Bazaar.Client.Commands.Tests;

public class FuzzyCoreTests
{
    [Fact]
    public void Blank_query_matches_everything_with_zero_score()
    {
        var result = FuzzyCore.Match("  ", "anything");
        Assert.NotNull(result);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Positions);
    }

    [Fact]
    public void Match_ignores_case_and_reports_positions()
    {
        var result = FuzzyCore.Match("os", "Open Settings");
        Assert.NotNull(result);
        Assert.Equal([0, 5], result.Positions);
    }

    [Fact]
    public void Match_agrees_with_the_facade()
    {
        var core = FuzzyCore.Match("gcs", "Go to chats");
        var facade = FuzzyScorer.Match("gcs", "Go to chats");
        Assert.NotNull(core);
        Assert.NotNull(facade);
        Assert.Equal(facade.Score, core.Score);
        Assert.Equal(facade.Positions, core.Positions);
    }

    [Fact]
    public void Match_returns_null_when_not_a_subsequence()
    {
        Assert.Null(FuzzyCore.Match("zq", "open settings"));
    }

    [Theory]
    [InlineData("abc", "a1b2c3", true)]
    [InlineData("acb", "abc", false)]
    [InlineData("", "abc", true)]
    [InlineData("ABC", "abc", false)]
    public void IsSubsequence_is_ordinal_and_ordered(string needle, string haystack, bool expected)
    {
        Assert.Equal(expected, FuzzyCore.IsSubsequence(needle, haystack));
    }

    [Fact]
    public void Missing_symbols_fail_unless_skipping()
    {
        Assert.False(FuzzyCore.IsSubsequence("a-b", "ab"));
        Assert.True(FuzzyCore.IsSubsequence("a-b", "ab", skipMissingSymbols: true));
    }

    [Fact]
    public void Missing_letters_still_fail_when_skipping_symbols()
    {
        Assert.False(FuzzyCore.IsSubsequence("a-z", "ab", skipMissingSymbols: true));
    }
}
