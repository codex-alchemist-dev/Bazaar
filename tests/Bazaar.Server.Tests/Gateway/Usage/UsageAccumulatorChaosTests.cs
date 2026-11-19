using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Usage;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Gateway.Usage;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): an upstream reports usage that cannot be true (negative counts, thinking above
/// output). The usage row must never carry a negative count, because gw_usage rejects one (CHECK constraints in the gateway
/// migration) and the in-memory totals would subtract it from the agent's spend.
/// </summary>
public class UsageAccumulatorChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static UsageAccumulator Accumulator() =>
        new("agent-1", "turn-1", "freellmapi", "keyref-1", "groq/llama-3.3-70b", GatewayIdentity.Pooled, new FakeTimeProvider(_start), 7);

    public static TheoryData<GatewayTokenUsage> ImpossibleUsage() =>
    [
        new GatewayTokenUsage(Input: -1_000_000, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 5, Thinking: 0),
        new GatewayTokenUsage(Input: 10, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: -5_000_000, Thinking: 0),
        new GatewayTokenUsage(Input: 10, CacheWrite5m: -1, CacheWrite1h: 0, CacheRead: 0, Output: 5, Thinking: 0),
        new GatewayTokenUsage(Input: 10, CacheWrite5m: 0, CacheWrite1h: -1, CacheRead: 0, Output: 5, Thinking: 0),
        new GatewayTokenUsage(Input: 10, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: -1, Output: 5, Thinking: 0),
        new GatewayTokenUsage(Input: 10, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 5, Thinking: -1),
        new GatewayTokenUsage(Input: 10, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 5, Thinking: 6),
        new GatewayTokenUsage(Input: long.MinValue, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: long.MinValue, Thinking: long.MinValue),
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
        Assert.Equal(7, record.Tokens.Input);
        Assert.Equal(10, record.Tokens.Output);
        AssertRowFitsTheTable(record.Tokens);
    }

    [Fact]
    public void A_valid_report_after_an_impossible_one_is_used()
    {
        var accumulator = Accumulator();
        var valid = new GatewayTokenUsage(Input: 12, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 0, Output: 3, Thinking: 1);
        accumulator.Observe(new ModelEvent.UsageReported(new GatewayTokenUsage(-1, 0, 0, 0, -1, 0)));
        accumulator.Observe(new ModelEvent.UsageReported(valid));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "groq/llama-3.3-70b"));

        var record = accumulator.Build(null);

        Assert.False(record.Estimated);
        Assert.Equal(valid, record.Tokens);
    }

    [Fact]
    public void A_huge_but_well_formed_report_is_kept_as_reported()
    {
        // long.MaxValue is a lie too, but it is not negative: it is recorded, and the budget arithmetic must cope with it.
        var accumulator = Accumulator();
        var huge = new GatewayTokenUsage(long.MaxValue, 0, 0, 0, long.MaxValue, 0);
        accumulator.Observe(new ModelEvent.UsageReported(huge));
        accumulator.Observe(new ModelEvent.Done(StopReason.EndTurn, "groq/llama-3.3-70b"));

        var record = accumulator.Build(null);

        Assert.Equal(huge, record.Tokens);
        AssertRowFitsTheTable(record.Tokens);
    }

    private static void AssertRowFitsTheTable(GatewayTokenUsage tokens)
    {
        Assert.True(tokens.Input >= 0);
        Assert.True(tokens.CacheWrite5m >= 0);
        Assert.True(tokens.CacheWrite1h >= 0);
        Assert.True(tokens.CacheRead >= 0);
        Assert.True(tokens.Output >= 0);
        Assert.True(tokens.Thinking >= 0);
        Assert.True(tokens.Thinking <= tokens.Output);
    }
}
