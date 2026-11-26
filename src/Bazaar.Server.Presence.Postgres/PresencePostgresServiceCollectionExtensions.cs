using Bazaar.Server.Presence.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Presence.Postgres;

/// <summary>Registers the durable presence store.</summary>
public static class PresencePostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PgPresenceStore"/> as <see cref="IDurablePresenceStore"/> (TryAdd). The presence module then uses it as its
    /// store and layers the cache snapshot store on top. Needs an <c>NpgsqlDataSource</c> in the container.
    /// </summary>
    public static IServiceCollection AddBazaarPresencePostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDurablePresenceStore, PgPresenceStore>();
        return services;
    }
}
