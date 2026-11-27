using Bazaar.Server.Providers;
using Bazaar.Server.RateLimits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.RateLimits.Cache;

public static class CacheRateLimitServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CacheRateLimiter"/> as the one <see cref="IRateLimiter"/>. It replaces any registration made before it (the
    /// in-memory limiter, the GitHub module's private fallback), and those register with <c>TryAdd</c>, so it wins whatever order the
    /// components run in. The counter is the first registered <see cref="ICacheProvider"/> that also implements <see cref="ICacheCounter"/>.
    /// </summary>
    public static IServiceCollection AddBazaarCacheRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CacheRateLimiterOptions>().Bind(configuration.GetSection(CacheRateLimiterOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CacheRateLimiterOptions>, CacheRateLimiterOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.RemoveAll<IRateLimiter>();
        services.AddSingleton<IRateLimiter>(sp =>
        {
            var counter = new Lazy<ICacheCounter?>(() => sp.GetServices<ICacheProvider>().OfType<ICacheCounter>().FirstOrDefault());
            return new CacheRateLimiter(
                () => counter.Value,
                sp.GetRequiredService<IOptions<CacheRateLimiterOptions>>().Value,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CacheRateLimiter>>());
        });
        return services;
    }
}
