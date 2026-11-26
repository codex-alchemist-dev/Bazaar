using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Observatory.Postgres.Tests;

public class LaneObservedParserTests
{
    [Fact]
    public void Reads_prefixed_and_bare_keys_as_numbers_or_strings()
    {
        var s = LaneObservedParser.ToSnapshot("p", "haiku", QuotaState.Available, null, """
            {"anthropic-ratelimit-requests-remaining":"40","requests-limit":50,
             "anthropic-ratelimit-input-tokens-remaining":900,"input-tokens-limit":"1000"}
            """);

        Assert.Equal((40L, 50L, 900L, 1000L), (s.RemainingRequests, s.LimitRequests, s.RemainingInputTokens, s.LimitInputTokens));
        Assert.Null(s.RemainingOutputTokens);
        Assert.Null(s.LimitOutputTokens);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"requests-remaining\":\"lots\"}")]
    public void Missing_or_unreadable_observed_gives_no_figures(string json)
    {
        var s = LaneObservedParser.ToSnapshot("p", "m", QuotaState.Down, null, json);

        Assert.Null(s.RemainingRequests);
        Assert.Equal(QuotaState.Down, s.State);
    }

    [Fact]
    public void Workspace_id_is_never_surfaced()
    {
        var s = LaneObservedParser.ToSnapshot("p", "m", QuotaState.Available, null, """{"workspace-id":"ws_123","requests-limit":5}""");

        Assert.DoesNotContain("ws_123", s.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("available", QuotaState.Available)]
    [InlineData("rate_limited", QuotaState.RateLimited)]
    [InlineData("spend_capped", QuotaState.SpendCapped)]
    [InlineData("daily_exhausted", QuotaState.DailyExhausted)]
    [InlineData("down", QuotaState.Down)]
    public void State_words_map_to_the_enum(string word, QuotaState expected) =>
        Assert.Equal(expected, PostgresLaneReader.ParseState(word));

    [Fact]
    public void Unknown_state_word_throws() =>
        Assert.Throws<InvalidOperationException>(() => PostgresLaneReader.ParseState("weird"));
}
