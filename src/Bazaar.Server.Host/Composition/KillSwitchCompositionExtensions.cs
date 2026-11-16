using Bazaar.Approvals;
using Bazaar.Approvals.Db;
using Bazaar.Server.Host.Safety;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.KillSwitch.Postgres;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Kill switch, containment and the token budget store, on durable Postgres stores. Registers the durable implementations first, so
/// <c>AddAgentSafety</c>'s in-memory defaults (TryAdd) never take effect. Also adds the startup gate, which runs before any agent.
/// No in-memory fallback is registered for state that must survive a restart.
/// </summary>
public static class KillSwitchCompositionExtensions
{
    /// <summary>Registers the durable kill switch state, containment, nonce, owner key and budget stores, the fail-closed VM and token adapters, and the startup gate.</summary>
    public static IServiceCollection AddBazaarKillSwitch(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IKillSwitchStateStore, PostgresKillSwitchStateStore>();
        services.TryAddSingleton<IContainmentStateStore, PostgresContainmentStateStore>();
        services.TryAddSingleton<INonceStore, PostgresNonceStore>();
        services.TryAddSingleton<ITokenBudgetStore, PostgresTokenBudgetStore>();
        services.TryAddSingleton<IOwnerKeyRegistry, PostgresOwnerKeyRegistry>();
        services.TryAddSingleton<IVmPauser, UnavailableVmPauser>();
        services.TryAddSingleton<IAgentTokenRevoker, UnavailableTokenRevoker>();

        services.AddAgentSafety(configuration);
        services.AddHostedService<KillSwitchStartupGate>();
        return services;
    }
}
