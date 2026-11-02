using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gc.Consumers;

public static class GcConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the consumer cursor store, the consumer runner and the sample unread counter. Needs the GC reader from
    /// <c>AddGcLog</c> and an <c>NpgsqlDataSource</c>.
    /// </summary>
    public static IServiceCollection AddBazaarGcConsumers(this IServiceCollection services)
    {
        services.AddSingleton<IConsumerCursorStore, ConsumerCursorStore>();
        services.AddSingleton<GcConsumerRunner>();
        services.AddSingleton<UnreadCountConsumer>();
        return services;
    }
}
