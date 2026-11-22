using Bazaar.Server.Identity.Leases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Identity.Postgres;

/// <summary>Registers the durable credential lease store. Call it before any in-memory default, so the durable store wins.</summary>
public static class IdentityPostgresServiceCollectionExtensions
{
    /// <summary>Registers <see cref="PostgresCredentialLeaseStore"/> as <see cref="ICredentialLeaseStore"/> (TryAdd).</summary>
    public static IServiceCollection AddBazaarPostgresCredentialLeases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICredentialLeaseStore, PostgresCredentialLeaseStore>();
        return services;
    }
}
