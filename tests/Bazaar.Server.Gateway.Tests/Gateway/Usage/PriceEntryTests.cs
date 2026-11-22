using Bazaar.Server.Gateway.Usage;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class PriceEntryTests
{
    private static readonly DateOnly _day = new(2025, 1, 1);

    [Fact]
    public void Cache_write_5m_defaults_to_1_25x_input()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 2.00m, 8.00m);

        Assert.Equal(2.50m, price.CacheWrite5mRate);
    }

    [Fact]
    public void Cache_write_1h_defaults_to_2x_input()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 2.00m, 8.00m);

        Assert.Equal(4.00m, price.CacheWrite1hRate);
    }

    [Fact]
    public void Cache_read_defaults_to_the_input_rate()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 2.00m, 8.00m);

        Assert.Equal(2.00m, price.CacheReadRate);
    }

    [Fact]
    public void Set_cache_rates_override_the_defaults()
    {
        var price = new PriceEntry("anthropic-direct", "claude-haiku-5-5", _day, 2.00m, 8.00m,
            cacheReadPerMillion: 0.20m, cacheWrite5mPerMillion: 2.75m, cacheWrite1hPerMillion: 3.50m);

        Assert.Equal(0.20m, price.CacheReadRate);
        Assert.Equal(2.75m, price.CacheWrite5mRate);
        Assert.Equal(3.50m, price.CacheWrite1hRate);
    }

    [Fact]
    public void Zero_prices_are_allowed()
    {
        var price = new PriceEntry("local", "llama", _day, 0m, 0m);

        Assert.Equal(0m, price.CacheReadRate);
    }

    [Fact]
    public void Negative_input_price_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceEntry("p", "m", _day, -0.01m, 1m));
    }

    [Fact]
    public void Negative_output_price_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceEntry("p", "m", _day, 1m, -1m));
    }

    [Fact]
    public void Negative_set_cache_prices_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceEntry("p", "m", _day, 1m, 1m, cacheReadPerMillion: -0.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceEntry("p", "m", _day, 1m, 1m, cacheWrite5mPerMillion: -0.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceEntry("p", "m", _day, 1m, 1m, cacheWrite1hPerMillion: -0.5m));
    }
}
