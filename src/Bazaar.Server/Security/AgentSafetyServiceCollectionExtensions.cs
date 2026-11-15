using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security;

/// <summary>Registration for the agent safety module: the kill switch, the token budgets and their adapters.</summary>
public static class AgentSafetyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="KillSwitchEngine"/>, the token budget service (through <see cref="TokenBudgetServiceCollectionExtensions.AddAgentTokenBudgets"/>),
    /// <see cref="KillSwitchAuditAdapter"/> and <see cref="AgentContainmentAdapter"/>, with the in-memory kill switch and
    /// containment stores unless the host registers durable ones (Postgres: Bazaar.Server.Security.KillSwitch.Postgres) first.
    /// All are singletons and use TryAdd, so a host registration made before this call wins.
    /// </summary>
    /// <remarks>
    /// The caller must register these first: <see cref="Bazaar.Server.Security.Audit.IAuditLog"/>,
    /// <see cref="Quarantine.IVmPauser"/>, <see cref="Quarantine.IAgentTokenRevoker"/>,
    /// a durable <see cref="IKillSwitchStateStore"/> and <see cref="IContainmentStateStore"/> so stops survive a restart,
    /// <see cref="Approvals.IOwnerKeyRegistry"/> (the owner check for release), and a durable
    /// <see cref="ITokenBudgetStore"/> so budget counters survive a restart. Nothing here registers them. The host is not
    /// changed by this call; it calls this method when it composes the server.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A configured token budget limit is below 1.</exception>
    public static IServiceCollection AddAgentSafety(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAgentTokenBudgets(configuration);
        services.TryAddSingleton<IKillSwitchStateStore, InMemoryKillSwitchStateStore>();
        services.TryAddSingleton<IContainmentStateStore, InMemoryContainmentStateStore>();
        services.TryAddSingleton<IKillSwitchAuditSink, KillSwitchAuditAdapter>();
        services.TryAddSingleton<INonceStore, InMemoryNonceStore>();
        services.TryAddSingleton<IAgentContainment, AgentContainmentAdapter>();
        services.TryAddSingleton<KillSwitchEngine>();
        return services;
    }
}
