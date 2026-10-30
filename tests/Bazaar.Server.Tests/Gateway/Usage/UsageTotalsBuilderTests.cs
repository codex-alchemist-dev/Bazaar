using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Usage;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class UsageTotalsBuilderTests
{
    private static readonly DateTimeOffset _at = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _day = new(2026, 1, 1);

    private static readonly PriceTable _prices = new("2026-01", [
        new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 1.00m, 5.00m),
    ]);

    [Fact]
    public void Cost_is_summed_over_priced_rows()
    {
        var records = new[]
        {
            Record(input: 1_000_000),
            Record(output: 1_000_000),
        };

        var (total, _) = UsageTotalsBuilder.Build(records, _prices);

        Assert.Equal(2, total.Calls);
        Assert.Equal(6m, total.CostUsd);
        Assert.True(total.IsCostKnown);
        Assert.Equal(0, total.UnpricedCalls);
    }

    [Fact]
    public void A_row_with_no_price_makes_the_cost_unknown_not_zero()
    {
        var records = new[]
        {
            Record(input: 1_000_000),
            Record(model: "claude-unlisted", input: 1_000_000),
        };

        var (total, _) = UsageTotalsBuilder.Build(records, _prices);

        Assert.Equal(1m, total.CostUsd);
        Assert.False(total.IsCostKnown);
        Assert.Equal(1, total.UnpricedCalls);
    }

    [Fact]
    public void No_price_table_means_every_row_is_unpriced()
    {
        var (total, _) = UsageTotalsBuilder.Build([Record(input: 10)], prices: null);

        Assert.Equal(0m, total.CostUsd);
        Assert.False(total.IsCostKnown);
    }

    [Fact]
    public void A_price_before_the_first_entry_is_unknown()
    {
        var early = Record(at: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), input: 10);

        var (total, _) = UsageTotalsBuilder.Build([early], _prices);

        Assert.False(total.IsCostKnown);
    }

    [Fact]
    public void Identity_split_gives_one_total_per_identity()
    {
        var records = new[]
        {
            Record(identity: GatewayIdentity.Native, input: 1_000_000),
            Record(identity: GatewayIdentity.Pooled, input: 2_000_000),
        };

        var (total, byIdentity) = UsageTotalsBuilder.Build(records, _prices);

        Assert.Equal(3m, total.CostUsd);
        Assert.Equal(1m, byIdentity[GatewayIdentity.Native].CostUsd);
        Assert.Equal(2m, byIdentity[GatewayIdentity.Pooled].CostUsd);
        Assert.Equal(1, byIdentity[GatewayIdentity.Native].Calls);
    }

    [Fact]
    public void Estimated_rows_are_counted_and_still_priced()
    {
        var (total, _) = UsageTotalsBuilder.Build([Record(input: 1_000_000, estimated: true)], _prices);

        Assert.Equal(1, total.EstimatedCalls);
        Assert.Equal(1m, total.CostUsd);
    }

    [Fact]
    public void Prompts_over_the_threshold_are_flagged_including_cache_tokens()
    {
        var records = new[]
        {
            Record(input: 60_000, cacheRead: 50_000),
            Record(input: 60_000),
        };

        var (total, _) = UsageTotalsBuilder.Build(records, _prices);

        Assert.Equal(1, total.LongPromptCalls);
    }

    [Fact]
    public void Errors_are_counted()
    {
        var (total, _) = UsageTotalsBuilder.Build([Record(status: UsageStatus.Error)], _prices);

        Assert.Equal(1, total.ErrorCalls);
    }

    [Fact]
    public void Empty_input_gives_empty_totals()
    {
        var (total, byIdentity) = UsageTotalsBuilder.Build([], _prices);

        Assert.Equal(UsageTotals.Empty, total);
        Assert.Empty(byIdentity);
        Assert.True(total.IsCostKnown);
    }

    private static GatewayUsageRecord Record(
        DateTimeOffset? at = null,
        string model = "claude-haiku-5-5",
        GatewayIdentity identity = GatewayIdentity.Native,
        UsageStatus status = UsageStatus.Success,
        bool estimated = false,
        long input = 0,
        long output = 0,
        long cacheRead = 0) => new(
            Id: Guid.NewGuid(),
            Timestamp: at ?? _at,
            AgentId: "charles",
            TurnId: null,
            ProviderId: "anthropic-direct",
            KeyRef: "secret://anthropic",
            ModelId: model,
            ServedModel: null,
            Identity: identity,
            Tokens: new GatewayTokenUsage(input, 0, 0, cacheRead, output, 0),
            LatencyMs: 1,
            Status: status,
            ErrorCode: null,
            RetryAfterSeconds: null,
            Estimated: estimated,
            PriceVersion: null);
}
