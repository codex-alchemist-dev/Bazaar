using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Components.RateLimits;

public static class RateLimitServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process rate limiter. Uses <see cref="TimeProvider.System"/> unless the host already registered one.
    /// </summary>
    // TODO(component): register from host composition when the component model (PR #125) merges.
    // The call belongs in the host composition root, next to the other component registrations. Not called yet.
    public static IServiceCollection AddBazaarRateLimits(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRateLimiter, InMemoryRateLimiter>();
        return services;
    }
}
