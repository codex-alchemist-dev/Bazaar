using Bazaar.Server.Skimmer.Shadow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Gc.Skimmer;

/// <summary>Registers the GC side of the Skimmer shadow gate.</summary>
public static class GcSkimmerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the message source and the hosted consumers. The host supplies <c>NpgsqlDataSource</c>, <c>IGcNotificationSource</c>,
    /// <c>IGcMessageTextSource</c>, <c>IShadowAgentDirectory</c>, <c>ShadowConsumer</c> and <see cref="GcShadowConsumerOptions"/>.
    /// </summary>
    public static IServiceCollection AddGcShadowConsumers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IShadowMessageSource, PgShadowMessageSource>();
        services.TryAddSingleton<GcShadowConsumerHost>();
        services.AddHostedService(sp => sp.GetRequiredService<GcShadowConsumerHost>());
        return services;
    }
}
