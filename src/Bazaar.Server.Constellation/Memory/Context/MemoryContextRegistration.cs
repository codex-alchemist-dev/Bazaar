using Bazaar.Server.Constellation.Memory.ReadLog;
using Bazaar.Server.Embeddings;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory.Context;

/// <summary>What the host passes to <see cref="MemoryContextRegistration.AddMemoryContext"/>, from its configuration.</summary>
/// <param name="VaultId">The Vault whose docs are the graph (<c>Constellation:VaultId</c>), or null for any Vault.</param>
/// <param name="Options">Retrieval tuning; null takes the defaults.</param>
/// <param name="EmbeddingTimeout">The query embedding timeout; null takes <see cref="EmbeddingMemoryVectorSeedSource.DefaultTimeout"/>.</param>
public sealed record MemoryContextHostOptions(Guid? VaultId = null, MemoryContextOptions? Options = null, TimeSpan? EmbeddingTimeout = null);

/// <summary>
/// Registers the retrieval operation (docs/design/constellation-memory-graph.md, "Retrieval operation"): the <c>memory.context.*</c>
/// settings, the vector seed source over the Embeddings contract, the graph retriever, <see cref="IMemoryContextService"/> and the
/// <c>memory.context</c> wire handler. The host calls it after <c>AddMemoryGraphProjection</c>.
/// </summary>
/// <remarks>
/// Resolved at first use: <see cref="NpgsqlDataSource"/> and <see cref="IAuditLog"/> are required. <see cref="IVaultAccess"/> defaults to
/// <see cref="VaultPathAccess"/> over the registered <see cref="IProjectMembership"/>, or over no memberships when none is registered. Vector
/// seeds are used when an <see cref="IActiveEmbeddingSelector"/> is registered (the Embeddings module); without one every call is lexical
/// only (<c>profile_none</c>). Limits come from <see cref="ISettingStore"/> when one is registered, otherwise the shipped defaults. An
/// <see cref="IMemoryAccessLog"/> is called for owner reads when one is registered.
/// </remarks>
public static class MemoryContextRegistration
{
    public static IServiceCollection AddMemoryContext(this IServiceCollection services, MemoryContextHostOptions? hostOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var host = hostOptions ?? new MemoryContextHostOptions();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, MemoryContextSettingContribution>());

        // The path rules of doc 08 section 8. No production project membership exists yet; without one, no agent is a member of any
        // project, so project areas are denied to agents (deny by default) while their own private area and the owner's reads work.
        services.TryAddSingleton<IVaultAccess>(sp => new VaultPathAccess(
            sp.GetService<IProjectMembership>() ?? new StaticProjectMembership(new Dictionary<string, ProjectMembers>())));
        services.TryAddSingleton<IMemoryContextLimitsSource>(sp => sp.GetService<ISettingStore>() is { } store
            ? new SettingStoreMemoryContextLimits(store)
            : new FixedMemoryContextLimits(MemoryContextLimits.Defaults));
        services.TryAddSingleton<IMemoryVectorQuery>(sp => new PgMemoryVectorQuery(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IMemoryContextReader>(sp => new PgMemoryContextReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => sp.GetService<IActiveEmbeddingSelector>() is { } selector
            ? new MemoryVectorSeedRegistration(new EmbeddingMemoryVectorSeedSource(
                selector,
                sp.GetRequiredService<IMemoryVectorQuery>(),
                sp.GetRequiredService<IVaultAccess>(),
                host.VaultId,
                host.EmbeddingTimeout,
                sp.GetService<ILogger<EmbeddingMemoryVectorSeedSource>>()))
            : new MemoryVectorSeedRegistration(null));
        services.TryAddSingleton(sp => new MemoryGraphRetriever(
            sp.GetRequiredService<IConstellationNodeReader>(),
            sp.GetRequiredService<IVaultAccess>(),
            sp.GetRequiredService<MemoryVectorSeedRegistration>().Source,
            sp.GetService<IMemoryReadLog>()));
        services.TryAddSingleton<IMemoryContextService>(sp => new MemoryContextService(
            sp.GetRequiredService<MemoryGraphRetriever>(),
            sp.GetRequiredService<IMemoryContextReader>(),
            sp.GetRequiredService<IVaultAccess>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<IMemoryContextLimitsSource>(),
            sp.GetService<TimeProvider>(),
            host.Options,
            sp.GetService<IMemoryAccessLog>(),
            host.VaultId));
        services.AddSingleton<IWireRequestHandler, MemoryContextWireHandler>();
        return services;
    }
}

/// <summary>The vector seed source, or none when no embedding selector is registered. A holder so the retriever can take a null source.</summary>
public sealed record MemoryVectorSeedRegistration(IMemoryVectorSeedSource? Source);
