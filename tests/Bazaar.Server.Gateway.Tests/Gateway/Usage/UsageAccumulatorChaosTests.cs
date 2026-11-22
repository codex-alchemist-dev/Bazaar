using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.Gateway.Usage;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Gateway.Usage;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): an upstream reports usage that cannot be true. Counts are exact
/// <see cref="TokenAmount"/>s (doc 48, DECIDED 11.3), so a negative count cannot even be built, and a huge one never wraps.
/// The usage row must never carry a count gw_usage would reject.
/// </summary>
public class UsageAccumulatorChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly TokenAmountBounds _wide = new(200_000, 0);

    private static UsageAccumulator Accumulator() =>
        new("agent-1", "turn-1", "freellmapi", "keyref-1", "groq/llama-3.3-70b", GatewayIdentity.Pooled, new FakeTimeProvider(_start), TokenAmount.FromInt64(7));

    private static TokenAmount BeyondNumeric() => TokenAmount.Parse(new string('9', 131_073), _wide);

    public static TheoryData<GatewayTokenUsage> ImpossibleUsage() =>
    [
        new GatewayTokenUsage(Input: 10, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 5, Thinking: 6),
        new GatewayTokenUsage(Input: 0, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 0, Thinking: long.MaxValue),
        new GatewayTokenUsage(Input: BeyondNumeric(), CacheWrite5m: TokenAmount.Zero, CacheWrite1h: TokenAmount.Zero, CacheRead: TokenAmount.Zero, Output: TokenAmount.FromInt64(5), Thinking: TokenAmount.Zero),
        new GatewayTokenUsage(Input: TokenAmount.Zero, CacheWrite5m: TokenAmount.Zero, CacheWrite1h: TokenAmount.Zero, CacheRead: TokenAmount.Zero, Output: BeyondNumeric(), Thinking: TokenAmount.One),
    ];

    [Theory]
    [MemberData(nameof(ImpossibleUsage))]
    public void An_impossible_usage_report_is_ignored_and_the_row_is_estimated(GatewayTokenUsage usage)
    {
        var accumulator = Accumulator();
        accumulator.Observe(new ModelEvent.TextDelta(new string('x', 40)));
        accumulator.Observe(new ModelEvent.UsageReported(usage));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "groq/llama-3.3-70b"));

        var record = accumulator.Build(null);

        Assert.True(record.Estimated);
        Assert.Equal(TokenAmount.FromInt64(7), record.Tokens.Input);
        Assert.Equal(TokenAmount.FromInt64(10), record.Tokens.Output);
        AssertRowFitsTheTable(record.Tokens);
    }

    [Fact]
    public void A_negative_count_cannot_be_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayTokenUsage(-1, 0, 0, 0, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenAmount.FromInt64(long.MinValue));
    }

    [Fact]
    public void A_valid_report_after_an_impossible_one_is_used()
    {
        var accumulator = Accumulator();
        var valid = new GatewayTokenUsage(Input: 12, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 3, Thinking: 1);
        accumulator.Observe(new ModelEvent.UsageReported(new GatewayTokenUsage(0, 0, 0, 0, 1, 2)));
        accumulator.Observe(new ModelEvent.UsageReported(valid));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "groq/llama-3.3-70b"));

        var record = accumulator.Build(null);

        Assert.False(record.Estimated);
        Assert.Equal(valid, record.Tokens);
    }

    [Fact]
    public void A_huge_but_well_formed_report_is_kept_as_reported()
    {
        // long.MaxValue is a lie too, but it is not negative: it is recorded exactly, and sums of it cannot wrap.
        var accumulator = Accumulator();
        var huge = new GatewayTokenUsage(long.MaxValue, 0, 0, 0, long.MaxValue, 0);
        accumulator.Observe(new ModelEvent.UsageReported(huge));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "groq/llama-3.3-70b"));

        var record = accumulator.Build(null);

        Assert.Equal(huge, record.Tokens);
        AssertRowFitsTheTable(record.Tokens);
    }

    [Fact]
    public void A_report_past_long_range_is_kept_exactly()
    {
        var accumulator = Accumulator();
        var beyondLong = TokenAmount.Parse("92233720368547758070000000000000000001"); // far above long.MaxValue
        var usage = new GatewayTokenUsage(beyondLong, TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Zero, TokenAmount.One, TokenAmount.Zero);
        accumulator.Observe(new ModelEvent.UsageReported(usage));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "groq/llama-3.3-70b"));

        var record = accumulator.Build(null);

        Assert.False(record.Estimated);
        Assert.Equal(beyondLong, record.Tokens.Input);
    }

    private static void AssertRowFitsTheTable(GatewayTokenUsage tokens)
    {
        Assert.True(tokens.Thinking <= tokens.Output);
        Assert.True(tokens.Input.Fits(TokenAmountBounds.PostgresNumeric));
        Assert.True(tokens.CacheWrite5m.Fits(TokenAmountBounds.PostgresNumeric));
        Assert.True(tokens.CacheWrite1h.Fits(TokenAmountBounds.PostgresNumeric));
        Assert.True(tokens.CacheRead.Fits(TokenAmountBounds.PostgresNumeric));
        Assert.True(tokens.Output.Fits(TokenAmountBounds.PostgresNumeric));
        Assert.True(tokens.Thinking.Fits(TokenAmountBounds.PostgresNumeric));
    }
}
