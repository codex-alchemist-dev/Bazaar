using Bazaar.Server.Council.Service;
using Bazaar.Server.Host.CouncilVault;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The Council module's Vault adapter in the host (C24, doc 10 Recording). It sits here, not in the Council module, because
/// <see cref="VaultWriter"/> is still inside <c>Bazaar.Server.csproj</c> and no module may reference that project (doc 40).
/// </summary>
public static class CouncilCompositionExtensions
{
    /// <summary>
    /// Registers <see cref="ICouncilDecisionWriter"/> as the Vault decision writer. The <see cref="VaultWriter"/> is resolved when
    /// the port is first requested, so the Vault component must be registered for a Council decision to be recorded.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilDecisionVault(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICouncilDecisionWriter>(sp =>
            new CouncilDecisionVaultAdapter(sp.GetRequiredService<VaultWriter>().CreateDecisionAsync));
        return services;
    }
}
