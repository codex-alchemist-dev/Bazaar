using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Identity.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Identity.Postgres;

/// <summary>Registers the durable identity stores. Call them before any in-memory default, so the durable store wins.</summary>
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

    /// <summary>Registers <see cref="PostgresSigningKeyStore"/> as <see cref="ISigningKeyStore"/> (TryAdd). Needs an <c>NpgsqlDataSource</c>.</summary>
    public static IServiceCollection AddBazaarPostgresSigningKeys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISigningKeyStore, PostgresSigningKeyStore>();
        return services;
    }
}
