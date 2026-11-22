using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Host.CouncilAdapters;
using Bazaar.Server.Host.CouncilVault;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The Council module's Vault adapter in the host (C24, doc 10 Recording). It sits here, not in the Council module, because
/// <see cref="VaultWriter"/> is still inside <c>Bazaar.Server.csproj</c> and no module may reference that project (doc 40).
/// </summary>
public static class CouncilCompositionExtensions
{
    /// <summary>
    /// Registers <see cref="ICouncilDecisionWriter"/> as the Vault decision writer. The <see cref="VaultWriter"/> is resolved when a
    /// decision is first recorded, not when the port is requested, so the Council service resolves on a host with no Vault
    /// repository configured (the Vault is a soft dependency of the Council component); recording a decision there fails with the
    /// container's missing-service message.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilDecisionVault(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICouncilDecisionWriter>(sp =>
            new CouncilDecisionVaultAdapter((draft, ct) => sp.GetRequiredService<VaultWriter>().CreateDecisionAsync(draft, ct)));
        return services;
    }

    /// <summary>
    /// Registers the host adapters for the Council ports that need Server or GC types (doc 40): <see cref="ICouncilGcEvents"/> over
    /// <see cref="IGcLogWriter"/>, <see cref="IGcMembership"/> over <c>gc_member_current</c>, and <see cref="ICouncilReasoningCipher"/>
    /// over <see cref="PersonKeyStore"/>. Each resolves its dependencies when first requested, so a host without a KEK provider fails
    /// at the first ballot, not at start. <c>TryAdd</c>, so a host or test can replace any of them.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilHostAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICouncilGcEvents>(sp => new CouncilGcEventsAdapter(sp.GetRequiredService<IGcLogWriter>()));
        services.TryAddSingleton<IGcMembership>(sp => new PgGcMembershipAdapter(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ICouncilReasoningCipher>(sp => new PersonKeyCouncilReasoningCipher(
            new PersonKeyStore(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IKekProvider>())));
        return services;
    }
}
