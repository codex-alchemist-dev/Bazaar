using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.RateLimits.Cache;

/// <summary>
/// The shared cache-backed rate limiter as a Bazaar Component (doc 15, "Shared cache-backed limiter", PROPOSED). It depends on no other
/// component: the cache modules are soft dependencies, and with none installed the limiter runs on its in-process fallback.
/// </summary>
public sealed class RateLimitsCacheComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "rate-limits-cache",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["rate-limiter:cache"],
        SoftDependsOn: ["valkey", "garnet"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarCacheRateLimits(configuration);
}
