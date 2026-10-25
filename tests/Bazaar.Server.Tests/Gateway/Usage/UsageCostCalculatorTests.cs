using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Usage;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class UsageCostCalculatorTests
{
    private static readonly DateOnly _day = new(2025, 1, 1);

    [Fact]
    public void Cost_adds_all_five_buckets_with_exact_decimals()
    {
        // Explicit rates: input 3.00, cache write 5m 3.75, cache write 1h 6.00, cache read 0.30, output 15.00 per million.
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 3.00m, 15.00m,
            cacheReadPerMillion: 0.30m, cacheWrite5mPerMillion: 3.75m, cacheWrite1hPerMillion: 6.00m);
        var tokens = new GatewayTokenUsage(Input: 1000, CacheWrite5m: 2000, CacheWrite1h: 500, CacheRead: 4000, Output: 1500, Thinking: 0);

        // 3000 + 7500 + 3000 + 1200 + 22500 = 37200 micro-dollars, so 0.0372 USD.
        Assert.Equal(0.0372m, UsageCostCalculator.Compute(tokens, price));
    }

    [Fact]
    public void Thinking_is_not_added_on_top_of_output()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 3.00m, 15.00m);
        var withoutThinking = new GatewayTokenUsage(0, 0, 0, 0, Output: 100_000, Thinking: 0);
        var withThinking = new GatewayTokenUsage(0, 0, 0, 0, Output: 100_000, Thinking: 40_000);
        var thinkingIsAllOutput = new GatewayTokenUsage(0, 0, 0, 0, Output: 100_000, Thinking: 100_000);

        var expected = UsageCostCalculator.Compute(withoutThinking, price);

        Assert.Equal(1.5m, expected);
        Assert.Equal(expected, UsageCostCalculator.Compute(withThinking, price));
        Assert.Equal(expected, UsageCostCalculator.Compute(thinkingIsAllOutput, price));
    }

    [Fact]
    public void Cache_write_5m_defaults_to_1_25x_input_in_the_cost()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 2.00m, 8.00m);
        var tokens = new GatewayTokenUsage(0, CacheWrite5m: 1_000_000, 0, 0, 0, 0);

        Assert.Equal(2.50m, UsageCostCalculator.Compute(tokens, price));
    }

    [Fact]
    public void Cache_write_1h_defaults_to_2x_input_in_the_cost()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 2.00m, 8.00m);
        var tokens = new GatewayTokenUsage(0, 0, CacheWrite1h: 1_000_000, 0, 0, 0);

        Assert.Equal(4.00m, UsageCostCalculator.Compute(tokens, price));
    }

    [Fact]
    public void Cache_read_defaults_to_the_input_rate_in_the_cost()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 3.00m, 15.00m);
        var tokens = new GatewayTokenUsage(0, 0, 0, CacheRead: 1_000_000, 0, 0);

        Assert.Equal(3.00m, UsageCostCalculator.Compute(tokens, price));
    }

    [Fact]
    public void No_price_gives_no_cost()
    {
        var tokens = new GatewayTokenUsage(1000, 0, 0, 0, 1000, 0);

        Assert.Null(UsageCostCalculator.Compute(tokens, null));
    }

    [Fact]
    public void Zero_tokens_cost_nothing()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 3.00m, 15.00m);

        Assert.Equal(0m, UsageCostCalculator.Compute(new GatewayTokenUsage(0, 0, 0, 0, 0, 0), price));
    }
}
