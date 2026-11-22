using Microsoft.Extensions.Options;

namespace Bazaar.RateLimits.Cache;

/// <summary>Settings for the shared cache-backed rate limiter (doc 15, "Shared cache-backed limiter", PROPOSED). Bound from <see cref="SectionName"/>.</summary>
public sealed class CacheRateLimiterOptions
{
    /// <summary>Configuration section, read from <c>appsettings.json</c> or <c>BAZAAR__</c> environment variables.</summary>
    public const string SectionName = "Bazaar:RateLimits";

    /// <summary>The longest allowed <see cref="CacheTimeout"/>. The limiter blocks the calling thread while it waits, so this stays short.</summary>
    public static readonly TimeSpan MaxCacheTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one limiter call waits for the cache before it falls back to the in-process limiter. The interface is synchronous,
    /// so this is the most a call can block for the cache; a denied call may wait once more to undo its count.
    /// </summary>
    public TimeSpan CacheTimeout { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Returns the reason the settings are invalid, or null when they are valid.</summary>
    public string? ValidationError() =>
        CacheTimeout <= TimeSpan.Zero || CacheTimeout > MaxCacheTimeout
            ? $"{SectionName}:CacheTimeout must be above zero and at most {MaxCacheTimeout.TotalSeconds:0} seconds."
            : null;
}

/// <summary>Fails host start when <see cref="CacheRateLimiterOptions"/> is invalid.</summary>
internal sealed class CacheRateLimiterOptionsValidator : IValidateOptions<CacheRateLimiterOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheRateLimiterOptions options) =>
        options.ValidationError() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
