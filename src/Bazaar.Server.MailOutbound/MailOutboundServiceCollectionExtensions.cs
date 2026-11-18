using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.MailOutbound;

public static class MailOutboundServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbound send policy. The ports (address directory, approved recipients, settings) and the shared
    /// <c>IRateLimiter</c> come from the host; a policy registered earlier is kept, so a test can substitute its own.
    /// </summary>
    public static IServiceCollection AddBazaarMailOutbound(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IOutboundMailPolicy, OutboundMailPolicy>();
        return services;
    }
}
