using Bazaar.Client.Core.Commands;

namespace Bazaar.Client.Core.Tests.Commands;

public class FuzzyScorerTests
{
    [Fact]
    public void Non_subsequence_does_not_match()
    {
        Assert.Null(FuzzyScorer.Match("xyz", "open settings"));
    }

    [Fact]
    public void Query_longer_than_candidate_does_not_match()
    {
        Assert.Null(FuzzyScorer.Match("abcdef", "abc"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var result = FuzzyScorer.Match("OPEN", "open file");

        Assert.NotNull(result);
        Assert.Equal([0, 1, 2, 3], result.Positions);
    }

    [Fact]
    public void Characters_must_appear_in_order()
    {
        Assert.Null(FuzzyScorer.Match("ba", "ab"));
    }

    [Fact]
    public void Blank_query_matches_with_no_positions()
    {
        var result = FuzzyScorer.Match("   ", "anything");

        Assert.NotNull(result);
        Assert.Empty(result.Positions);
    }

    [Fact]
    public void Returns_positions_of_every_matched_character()
    {
        var result = FuzzyScorer.Match("oc", "open chat");

        Assert.NotNull(result);
        Assert.Equal([0, 5], result.Positions);
    }

    [Fact]
    public void Consecutive_run_scores_higher_than_scattered_match()
    {
        var run = FuzzyScorer.Match("set", "settings");
        var scattered = FuzzyScorer.Match("set", "s-e-t-tings");

        Assert.NotNull(run);
        Assert.NotNull(scattered);
        Assert.True(run.Score > scattered.Score);
    }

    [Fact]
    public void Word_start_hit_scores_higher_than_mid_word_hit()
    {
        var wordStart = FuzzyScorer.Match("c", "open chat");
        var midWord = FuzzyScorer.Match("c", "abacus");

        Assert.NotNull(wordStart);
        Assert.NotNull(midWord);
        Assert.True(wordStart.Score > midWord.Score);
    }

    [Fact]
    public void Shorter_candidate_scores_higher_for_the_same_alignment()
    {
        var shorter = FuzzyScorer.Match("ab", "ab");
        var longer = FuzzyScorer.Match("ab", "ab and a lot more text");

        Assert.NotNull(shorter);
        Assert.NotNull(longer);
        Assert.True(shorter.Score > longer.Score);
    }

    [Fact]
    public void Best_alignment_is_found_not_just_the_first_greedy_one()
    {
        // Greedy matching would take the 'a' at index 0 and score a gap-heavy path.
        // The optimal path uses the consecutive "ab" at the end.
        var result = FuzzyScorer.Match("ab", "a x ab");

        Assert.NotNull(result);
        Assert.Equal([4, 5], result.Positions);
    }
}
