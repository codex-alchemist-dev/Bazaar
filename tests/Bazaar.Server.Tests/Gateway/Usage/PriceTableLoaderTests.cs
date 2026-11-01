using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Usage;

namespace Bazaar.Server.Tests.Gateway.Usage;

public class PriceTableLoaderTests
{
    private const string _valid = """
        {
          "version": "2026-10",
          "entries": [
            { "provider": "anthropic-direct", "model": "claude-haiku-5-5", "effective_from": "2026-01-01",
              "input_per_mtok": 1.00, "output_per_mtok": 5.00 },
            { "provider": "anthropic-direct", "model": "claude-haiku-5-5", "effective_from": "2026-06-01",
              "input_per_mtok": 1.50, "output_per_mtok": 7.50, "cache_read_per_mtok": 0.10 }
          ]
        }
        """;

    [Fact]
    public void Parses_version_and_entries()
    {
        var table = PriceTableLoader.Parse(_valid);

        Assert.Equal("2026-10", table.Version);
    }

    [Fact]
    public void Lookup_picks_the_entry_in_force_on_the_call_date()
    {
        var table = PriceTableLoader.Parse(_valid);

        var before = table.Lookup("anthropic-direct", "claude-haiku-5-5", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var after = table.Lookup("anthropic-direct", "claude-haiku-5-5", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1.00m, before!.InputPerMillion);
        Assert.Equal(1.50m, after!.InputPerMillion);
        Assert.Equal(0.10m, after.CacheReadRate);
    }

    [Fact]
    public void Absent_cache_rates_take_the_documented_defaults()
    {
        var table = PriceTableLoader.Parse(_valid);

        var entry = table.Lookup("anthropic-direct", "claude-haiku-5-5", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1.25m, entry!.CacheWrite5mRate);
        Assert.Equal(2.00m, entry.CacheWrite1hRate);
    }

    [Fact]
    public void Unknown_property_is_rejected()
    {
        const string json = """
            { "version": "v", "entries": [ { "provider": "p", "model": "m", "effective_from": "2026-01-01",
              "input_per_mtok": 1, "output_per_mtok": 1, "input_per_mtk": 9 } ] }
            """;

        Assert.Throws<InvalidDataException>(() => PriceTableLoader.Parse(json));
    }

    [Fact]
    public void Bad_date_is_rejected()
    {
        const string json = """
            { "version": "v", "entries": [ { "provider": "p", "model": "m", "effective_from": "01/02/2026",
              "input_per_mtok": 1, "output_per_mtok": 1 } ] }
            """;

        Assert.Throws<InvalidDataException>(() => PriceTableLoader.Parse(json));
    }

    [Fact]
    public void Negative_price_is_rejected()
    {
        const string json = """
            { "version": "v", "entries": [ { "provider": "p", "model": "m", "effective_from": "2026-01-01",
              "input_per_mtok": -1, "output_per_mtok": 1 } ] }
            """;

        Assert.Throws<InvalidDataException>(() => PriceTableLoader.Parse(json));
    }

    [Fact]
    public void Duplicate_entry_is_rejected()
    {
        const string json = """
            { "version": "v", "entries": [
              { "provider": "p", "model": "m", "effective_from": "2026-01-01", "input_per_mtok": 1, "output_per_mtok": 1 },
              { "provider": "p", "model": "m", "effective_from": "2026-01-01", "input_per_mtok": 2, "output_per_mtok": 2 } ] }
            """;

        Assert.Throws<InvalidDataException>(() => PriceTableLoader.Parse(json));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => PriceTableLoader.Parse("{ not json"));
    }
}
