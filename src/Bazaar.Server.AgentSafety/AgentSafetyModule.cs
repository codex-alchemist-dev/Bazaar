using Bazaar.Approvals;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.AgentSafety.Startup;
using Bazaar.Server.AgentSafety.Wake;
using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.KillSwitch.Postgres;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.AgentSafety;

/// <summary>
/// The agent safety module's composition: durable stores, the real containment adapters, and the startup gate. The host calls this
/// once and holds no feature code itself.
/// </summary>
/// <remarks>
/// Durable implementations are registered before <c>AddAgentSafety</c>, so its in-memory defaults (TryAdd) never take effect.
/// The VM pauser is real when the Vms lifecycle is composed; otherwise it is fail closed. The token revoker is real and runs over
/// the Identity lease store.
/// </remarks>
public static class AgentSafetyModule
{
    /// <summary>Registers the module. Expects the shared <c>NpgsqlDataSource</c> and the Identity lease store to be registered already.</summary>
    public static IServiceCollection AddBazaarAgentSafetyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IKillSwitchStateStore, PostgresKillSwitchStateStore>();
        services.TryAddSingleton<IContainmentStateStore, PostgresContainmentStateStore>();
        services.TryAddSingleton<INonceStore, PostgresNonceStore>();
        services.TryAddSingleton<ITokenBudgetStore, PostgresTokenBudgetStore>();
        services.AddBazaarApprovals();
        services.TryAddSingleton<IAgentTokenRevoker>(sp => new LeaseTokenRevoker(sp.GetRequiredService<ICredentialLeaseStore>()));
        services.TryAddSingleton<IVmPauser>(sp =>
            sp.GetService<VmLifecycleService>() is { } lifecycle
            && sp.GetService<IVmBackend>() is { } backend
            && sp.GetService<IAgentVmDirectory>() is { } vms
                ? new VmContainmentPauser(vms, lifecycle, backend)
                : new UnavailableVmPauser());

        services.AddAgentSafety(configuration);
        services.AddHostedService<KillSwitchStartupGate>();
        // The wake loop (doc 06) asks the kill switch before every wake. Replace, so the answer does not depend on registration order.
        services.Replace(ServiceDescriptor.Singleton<IAgentWakeGate>(sp => new KillSwitchWakeGate(sp.GetRequiredService<KillSwitchEngine>())));
        return services;
    }
}
