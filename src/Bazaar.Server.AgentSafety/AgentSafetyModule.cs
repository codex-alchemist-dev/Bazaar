using Bazaar.Approvals;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Server.AgentSafety.AutoContainment;
using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.AgentSafety.Startup;
using Bazaar.Server.AgentSafety.Wake;
using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.KillSwitch.Postgres;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AgentSafety;

/// <summary>
/// The agent safety module's composition: durable stores, the real containment adapters, and the startup gate. The host calls this
/// once and holds no feature code itself.
/// </summary>
/// <remarks>
/// Durable implementations are registered before <c>AddAgentSafety</c>, so its in-memory defaults (TryAdd) never take effect.
/// The VM pauser is real when the Vms lifecycle is composed; otherwise it is fail closed. The token revoker is real and runs over
/// the Identity lease store. A containment enforcer starts after the gate and re-applies containment for agents contained before the start.
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
        services.TryAddSingleton<IAgentContainmentCheck, StoreAgentContainmentCheck>();
        services.TryAddSingleton<IAgentTokenRevoker>(sp => new LeaseTokenRevoker(sp.GetRequiredService<ICredentialLeaseStore>()));
        services.TryAddSingleton<IVmPauser>(sp =>
            sp.GetService<VmLifecycleService>() is { } lifecycle
            && sp.GetService<IVmBackend>() is { } backend
            && sp.GetService<IAgentVmDirectory>() is { } vms
                ? new VmContainmentPauser(vms, lifecycle, backend)
                : new UnavailableVmPauser());

        // Contain also blocks egress and detaches mounts: the real adapter is wrapped by the network and mount decorator. Replace, not
        // TryAdd, so the result does not depend on whether AddAgentSafety, the host or VmMounts registered first (their defaults are
        // TryAdds); a registration made after this call is a deliberate host override and wins. Modules that are not composed are
        // handled by the decorator: loud on contain, "nothing to lift" on release. Tune GuestScrubOptions and ContainmentOptions with
        // services.Configure.
        services.AddOptions<GuestScrubOptions>();
        services.AddOptions<ContainmentOptions>();
        services.Replace(ServiceDescriptor.Singleton<IAgentContainment>(sp =>
        {
            var revoker = sp.GetRequiredService<IAgentTokenRevoker>();
            var audit = sp.GetRequiredService<IAuditLog>();
            var time = sp.GetService<TimeProvider>();
            var adapter = new AgentContainmentAdapter(
                sp.GetRequiredService<IVmPauser>(),
                revoker,
                sp.GetRequiredService<IOwnerKeyRegistry>(),
                audit,
                sp.GetRequiredService<INonceStore>(),
                sp.GetRequiredService<IContainmentStateStore>(),
                time);
            return new NetworkAndMountContainment(
                adapter,
                revoker,
                sp.GetService<IEgressPolicy>(),
                sp.GetService<IMountManager>(),
                audit,
                sp.GetRequiredService<IOptions<ContainmentOptions>>().Value,
                time,
                sp.GetService<ILogger<NetworkAndMountContainment>>());
        }));
        services.Replace(ServiceDescriptor.Singleton<IGuestScrubber>(sp =>
            sp.GetService<VmLifecycleService>() is { } lifecycle
                ? new VmGuestScrubber(lifecycle, sp.GetRequiredService<IOptions<GuestScrubOptions>>().Value)
                : new UnavailableGuestScrubber()));

        services.AddAgentSafety(configuration);
        services.TryAddSingleton<ICanarySink, CanaryContainmentSink>();
        services.AddHostedService<KillSwitchStartupGate>();

        // After the gate: re-applies containment for agents that were contained before this start.
        services.AddHostedService(sp => new ContainmentEnforcer(
            sp.GetRequiredService<IContainmentStateStore>(),
            sp.GetRequiredService<IAgentTokenRevoker>(),
            sp.GetRequiredService<IVmPauser>(),
            sp.GetRequiredService<ILogger<ContainmentEnforcer>>(),
            sp.GetService<TimeProvider>(),
            releaseGuard: sp.GetService<ContainmentReleaseGuard>()));
        services.AddBazaarAnomalyDetection();
        services.TryAddSingleton<IContainmentRecordStore, PostgresContainmentRecordStore>();
        services.AddBazaarAutoContainment();
        // The wake loop (doc 06) asks the kill switch before every wake. Replace, so the answer does not depend on registration order.
        services.Replace(ServiceDescriptor.Singleton<IAgentWakeGate>(sp => new KillSwitchWakeGate(sp.GetRequiredService<KillSwitchEngine>())));
        return services;
    }
}
