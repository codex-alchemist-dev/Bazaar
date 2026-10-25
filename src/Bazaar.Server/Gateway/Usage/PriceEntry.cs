namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// One provider model's list price from a date on, in USD per million tokens. Unset cache rates take the
/// documented defaults: cache writes at 1.25x input (5 minute) and 2x input (1 hour), and cache reads at the
/// input rate, which never undercounts.
/// </summary>
public sealed record PriceEntry
{
    /// <summary>Validates the prices: every rate must be zero or more.</summary>
    public PriceEntry(
        string providerId,
        string modelId,
        DateOnly effectiveFrom,
        decimal inputPerMillion,
        decimal outputPerMillion,
        decimal? cacheReadPerMillion = null,
        decimal? cacheWrite5mPerMillion = null,
        decimal? cacheWrite1hPerMillion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        RequireNonNegative(inputPerMillion, nameof(inputPerMillion));
        RequireNonNegative(outputPerMillion, nameof(outputPerMillion));
        if (cacheReadPerMillion is { } read)
        {
            RequireNonNegative(read, nameof(cacheReadPerMillion));
        }

        if (cacheWrite5mPerMillion is { } write5m)
        {
            RequireNonNegative(write5m, nameof(cacheWrite5mPerMillion));
        }

        if (cacheWrite1hPerMillion is { } write1h)
        {
            RequireNonNegative(write1h, nameof(cacheWrite1hPerMillion));
        }

        ProviderId = providerId;
        ModelId = modelId;
        EffectiveFrom = effectiveFrom;
        InputPerMillion = inputPerMillion;
        OutputPerMillion = outputPerMillion;
        CacheReadPerMillion = cacheReadPerMillion;
        CacheWrite5mPerMillion = cacheWrite5mPerMillion;
        CacheWrite1hPerMillion = cacheWrite1hPerMillion;
    }

    /// <summary>The provider the price applies to.</summary>
    public string ProviderId { get; }

    /// <summary>The model the price applies to.</summary>
    public string ModelId { get; }

    /// <summary>The first UTC date the price applies to.</summary>
    public DateOnly EffectiveFrom { get; }

    /// <summary>Uncached input rate.</summary>
    public decimal InputPerMillion { get; }

    /// <summary>Output rate. Thinking is billed through this rate as well.</summary>
    public decimal OutputPerMillion { get; }

    /// <summary>Cache read rate as set, or null to use the input rate.</summary>
    public decimal? CacheReadPerMillion { get; }

    /// <summary>5 minute cache write rate as set, or null to use 1.25x input.</summary>
    public decimal? CacheWrite5mPerMillion { get; }

    /// <summary>1 hour cache write rate as set, or null to use 2x input.</summary>
    public decimal? CacheWrite1hPerMillion { get; }

    /// <summary>The cache read rate in force: the set value, or the input rate when unset.</summary>
    public decimal CacheReadRate => CacheReadPerMillion ?? InputPerMillion;

    /// <summary>The 5 minute cache write rate in force: the set value, or 1.25x input when unset.</summary>
    public decimal CacheWrite5mRate => CacheWrite5mPerMillion ?? InputPerMillion * 1.25m;

    /// <summary>The 1 hour cache write rate in force: the set value, or 2x input when unset.</summary>
    public decimal CacheWrite1hRate => CacheWrite1hPerMillion ?? InputPerMillion * 2m;

    private static void RequireNonNegative(decimal value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A price cannot be negative.");
        }
    }
}
