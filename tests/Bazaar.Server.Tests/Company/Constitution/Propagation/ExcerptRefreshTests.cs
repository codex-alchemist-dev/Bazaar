using Bazaar.Server.Company.Constitution.Propagation;

namespace Bazaar.Server.Tests.Company.Constitution.Propagation;

public class ExcerptRefreshTests
{
    private const string _turnSha = "1111111111111111111111111111111111111111";
    private const string _activeSha = "2222222222222222222222222222222222222222";

    private static KeyValuePair<string, string> Section(string name, string body) => new(name, body);

    [Fact]
    public void Concatenates_sections_in_order_under_headings()
    {
        var result = ExcerptRefresh.Build([Section("Values", "Be kind."), Section("Escalation", "Ask the owner.")]);

        Assert.False(result.Truncated);
        Assert.Equal("## Values\nBe kind.\n\n## Escalation\nAsk the owner.\n\n", result.Text);
    }

    [Fact]
    public void Default_budget_is_1500_tokens()
    {
        Assert.Equal(1500, ExcerptRefresh.DefaultTokenBudget);
    }

    [Fact]
    public void Text_within_budget_is_not_marked_truncated()
    {
        var result = ExcerptRefresh.Build([Section("Values", "Short.")], tokenBudget: 100);

        Assert.False(result.Truncated);
        Assert.DoesNotContain(ExcerptRefresh.TruncationMarker, result.Text);
    }

    [Fact]
    public void Long_text_is_cut_to_the_budget_and_marked()
    {
        var body = new string('x', 10_000);

        var result = ExcerptRefresh.Build([Section("Values", body)], tokenBudget: 10);

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= 10 * ExcerptRefresh.CharsPerToken);
        Assert.EndsWith(ExcerptRefresh.TruncationMarker, result.Text);
        Assert.True(result.EstimatedTokens <= 10);
    }

    [Fact]
    public void Default_budget_cut_keeps_the_excerpt_within_6000_characters()
    {
        var body = new string('y', 20_000);

        var result = ExcerptRefresh.Build([Section("Values", body)]);

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= 6000);
        Assert.True(result.EstimatedTokens <= 1500);
    }

    [Fact]
    public void Tiny_budget_returns_no_more_than_the_budget()
    {
        var result = ExcerptRefresh.Build([Section("Values", new string('z', 500))], tokenBudget: 1);

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= ExcerptRefresh.CharsPerToken);
        Assert.True(result.EstimatedTokens <= 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Budget_must_be_positive(int budget)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExcerptRefresh.Build([Section("Values", "text")], budget));

        Assert.Equal("tokenBudget", ex.ParamName);
    }

    [Fact]
    public void Running_turn_keeps_its_old_commit()
    {
        var sha = ExcerptRefresh.ForTurn(_turnSha, _activeSha, nextTurnStarts: false);

        Assert.Equal(_turnSha, sha);
    }

    [Fact]
    public void Next_turn_uses_the_new_commit()
    {
        var sha = ExcerptRefresh.ForTurn(_turnSha, _activeSha, nextTurnStarts: true);

        Assert.Equal(_activeSha, sha);
    }

    [Fact]
    public void ForTurn_refuses_malformed_shas()
    {
        Assert.Throws<ArgumentException>(() => ExcerptRefresh.ForTurn("short", _activeSha, false));
        Assert.Throws<ArgumentException>(() => ExcerptRefresh.ForTurn(_turnSha, "ABC", true));
    }
}
