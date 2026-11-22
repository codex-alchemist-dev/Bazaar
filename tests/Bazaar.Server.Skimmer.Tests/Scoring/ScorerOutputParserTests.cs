using Bazaar.Server.Skimmer.Scoring;

namespace Bazaar.Server.Tests.Skimmer.Scoring;

public class ScorerOutputParserTests
{
    private static readonly Guid _alpha = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");
    private static readonly Guid _beta = Guid.Parse("0196a6f0-0000-7000-8000-000000000202");
    private static readonly IReadOnlyList<Guid> _requested = [_alpha, _beta];

    private static string Item(Guid agent, string category = "question", string score = "7200") =>
        $"{{\"agent\":\"{agent:D}\",\"category\":\"{category}\",\"relevance_bp\":{score}}}";

    private static string Array(params string[] items) => "[" + string.Join(",", items) + "]";

    [Fact]
    public void Accepts_a_valid_array_with_one_object_per_agent()
    {
        var result = ScorerOutputParser.Parse(Array(Item(_alpha, "review", "0"), Item(_beta, "other", "10000")), _requested);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Reason);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(new RelevanceResult(_alpha, Category.Review, 0), result.Results[0]);
        Assert.Equal(new RelevanceResult(_beta, Category.Other, 10000), result.Results[1]);
    }

    [Fact]
    public void Accepts_an_empty_array_when_no_agents_were_requested()
    {
        var result = ScorerOutputParser.Parse("[]", []);

        Assert.True(result.Success);
        Assert.Empty(result.Results);
    }

    [Theory]
    [InlineData("{\"agent\":\"x\"}")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Rejects_a_non_array_root(string json)
    {
        var result = ScorerOutputParser.Parse(json, _requested);

        Assert.False(result.Success);
        Assert.Empty(result.Results);
        Assert.Equal("root is not a JSON array", result.Reason);
    }

    [Fact]
    public void Rejects_an_extra_key()
    {
        var item = $"{{\"agent\":\"{_alpha:D}\",\"category\":\"chat\",\"relevance_bp\":1,\"note\":\"hi\"}}";

        var result = ScorerOutputParser.Parse(Array(item, Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Empty(result.Results);
        Assert.Equal("unexpected key", result.Reason);
    }

    [Theory]
    [InlineData("{\"agent\":\"{0}\",\"category\":\"chat\"}")]
    [InlineData("{\"category\":\"chat\",\"relevance_bp\":1}")]
    public void Rejects_a_missing_key(string template)
    {
        var item = template.Replace("{0}", _alpha.ToString("D"));

        var result = ScorerOutputParser.Parse(Array(item, Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("missing key", result.Reason);
    }

    [Fact]
    public void Rejects_a_duplicate_key()
    {
        var item = $"{{\"agent\":\"{_alpha:D}\",\"agent\":\"{_alpha:D}\",\"category\":\"chat\",\"relevance_bp\":1}}";

        var result = ScorerOutputParser.Parse(Array(item, Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("duplicate key", result.Reason);
    }

    [Theory]
    [InlineData("Question")]
    [InlineData("QUESTION")]
    [InlineData("chatter")]
    [InlineData("")]
    public void Rejects_a_category_outside_the_closed_set(string category)
    {
        var result = ScorerOutputParser.Parse(Array(Item(_alpha, category), Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("unknown category", result.Reason);
    }

    [Fact]
    public void Rejects_relevance_given_as_a_string()
    {
        var item = $"{{\"agent\":\"{_alpha:D}\",\"category\":\"chat\",\"relevance_bp\":\"7200\"}}";

        var result = ScorerOutputParser.Parse(Array(item, Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("relevance_bp is not an integer", result.Reason);
    }

    [Theory]
    [InlineData("7200.5")]
    [InlineData("1e3")]
    [InlineData("true")]
    [InlineData("null")]
    public void Rejects_relevance_that_is_not_an_integer(string score)
    {
        var item = $"{{\"agent\":\"{_alpha:D}\",\"category\":\"chat\",\"relevance_bp\":{score}}}";

        var result = ScorerOutputParser.Parse(Array(item, Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("relevance_bp is not an integer", result.Reason);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("10001")]
    public void Rejects_relevance_out_of_range(string score)
    {
        var result = ScorerOutputParser.Parse(Array(Item(_alpha, "chat", score), Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("relevance_bp is out of range", result.Reason);
    }

    [Fact]
    public void Rejects_an_agent_that_was_not_requested()
    {
        var stranger = Guid.Parse("0196a6f0-0000-7000-8000-0000000009ff");

        var result = ScorerOutputParser.Parse(Array(Item(_alpha), Item(stranger)), _requested);

        Assert.False(result.Success);
        Assert.Equal("unknown agent", result.Reason);
    }

    [Fact]
    public void Rejects_an_agent_that_is_not_a_guid()
    {
        var item = "{\"agent\":\"not-a-guid\",\"category\":\"chat\",\"relevance_bp\":1}";

        var result = ScorerOutputParser.Parse(Array(item, Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("agent is not a GUID string", result.Reason);
    }

    [Fact]
    public void Rejects_a_duplicate_agent()
    {
        var result = ScorerOutputParser.Parse(Array(Item(_alpha), Item(_alpha), Item(_beta)), _requested);

        Assert.False(result.Success);
        Assert.Equal("duplicate agent", result.Reason);
    }

    [Fact]
    public void Rejects_a_missing_agent()
    {
        var result = ScorerOutputParser.Parse(Array(Item(_alpha)), _requested);

        Assert.False(result.Success);
        Assert.Empty(result.Results);
        Assert.Equal("missing agent", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("[{\"agent\":")]
    [InlineData("not json")]
    public void Rejects_invalid_json_without_throwing(string json)
    {
        var result = ScorerOutputParser.Parse(json, _requested);

        Assert.False(result.Success);
        Assert.Empty(result.Results);
        Assert.Equal("invalid JSON", result.Reason);
    }

    [Fact]
    public void Rejects_an_element_that_is_not_an_object()
    {
        var result = ScorerOutputParser.Parse(Array("\"" + _alpha.ToString("D") + "\""), _requested);

        Assert.False(result.Success);
        Assert.Equal("element is not an object", result.Reason);
    }
}
