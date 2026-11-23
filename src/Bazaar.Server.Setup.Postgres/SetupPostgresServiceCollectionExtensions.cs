using Bazaar.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>Registers the durable setup services. The caller registers the shared <see cref="NpgsqlDataSource"/> first.</summary>
public static class SetupPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresSetupInstanceLock"/>, <see cref="PostgresApplyResultStore"/>, <see cref="PostgresInstallIdProvider"/>
    /// and the setup secret writer (TryAdd, so a caller's own wins). The writer is <see cref="SecretStoreSetupSecretWriter"/> when an
    /// <see cref="ISecretStore"/> is registered when it is first resolved, otherwise a writer that reports no store and stores nothing.
    /// </summary>
    public static IServiceCollection AddBazaarSetupPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISetupInstanceLock, PostgresSetupInstanceLock>();
        services.TryAddSingleton<IApplyResultStore, PostgresApplyResultStore>();
        services.TryAddSingleton<IInstallIdProvider>(sp => new PostgresInstallIdProvider(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ISetupSecretWriter>(sp =>
            sp.GetService<ISecretStore>() is { } store ? new SecretStoreSetupSecretWriter(store) : new NoSecretStoreWriter());
        return services;
    }

    /// <summary>Fail closed: this install has no secret store, so nothing is stored and a write throws without naming the value.</summary>
    private sealed class NoSecretStoreWriter : ISetupSecretWriter
    {
        public bool StoreAvailable => false;

        public Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No secret store is registered in this install.");
    }
}
