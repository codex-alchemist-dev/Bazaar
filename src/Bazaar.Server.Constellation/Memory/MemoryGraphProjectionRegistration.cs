using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Registers the memory graph (docs/design/constellation-memory-graph.md): the graph store, its audited wrapper, and the
/// <see cref="IMemoryBatchSink"/> decorator that projects each committed memory write into the graph.
/// </summary>
/// <remarks>
/// The Memory module registers the plain Vault sink and never references this project. The decorator is order-independent:
/// it replaces whatever <see cref="IMemoryBatchSink"/> registration exists, and it resolves <see cref="IMemoryVault"/> and the
/// audit log lazily, at first use. So it gives the same sink whether the Memory module registered before or after it. Without a
/// Memory registration, resolving the sink fails at first use, as it did when nothing was registered.
/// </remarks>
public static class MemoryGraphProjectionRegistration
{
    /// <summary>
    /// Adds the graph store, the audited store and the graph sink decorator. Call it once, after the component registration.
    /// </summary>
    public static IServiceCollection AddMemoryGraphProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryGraphStore>(sp => new PgMemoryGraphStore(sp.GetRequiredService<NpgsqlDataSource>()));

        // Every graph change made through the memory path is audited (doc 19) when an audit log exists. Callers that roll a note
        // back take this store so the rollback is audited too.
        services.TryAddSingleton(sp => new AuditingMemoryGraphStore(
            sp.GetRequiredService<IMemoryGraphStore>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        // Writes go to the Vault first; the graph sink then projects them. Both are resolved at first use, so the registration
        // order of the Memory component does not matter. A later Memory registration of the plain sink is a no-op (TryAdd).
        services.Replace(ServiceDescriptor.Singleton<IMemoryBatchSink>(sp => MemoryGraphSink.Wrap(
            sp.GetRequiredService<IMemoryVault>(),
            sp.GetService<IAuditLog>() is not null ? sp.GetRequiredService<AuditingMemoryGraphStore>() : sp.GetRequiredService<IMemoryGraphStore>(),
            sp.GetRequiredService<ILogger<MemoryGraphSink>>())));
        return services;
    }
}
