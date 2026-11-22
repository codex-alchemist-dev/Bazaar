using Bazaar.Server.Agents.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Responder;

/// <summary>Registers the start-time default policy grant repair (doc 45, section 1.15).</summary>
public static class AgentGrantRepairServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AgentGrantRepair"/> and a start hook that runs <see cref="AgentGrantRepair.RepairAsync"/> once. The grant
    /// defaults to <see cref="NoAgentDefaultPolicyGrant"/>. The host must register an <see cref="AgentRepository"/> (the agent id
    /// source) and an <see cref="IAgentPolicyPresence"/> for any repair to happen.
    /// </summary>
    public static IServiceCollection AddBazaarAgentGrantRepair(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentDefaultPolicyGrant>(NoAgentDefaultPolicyGrant.Instance);
        services.TryAddSingleton(sp => new AgentGrantRepair(
            sp.GetRequiredService<IAgentDefaultPolicyGrant>(),
            sp.GetService<IAgentPolicyPresence>(),
            ct => sp.GetRequiredService<AgentRepository>().ListIdsAsync(ct),
            sp.GetRequiredService<ILogger<AgentGrantRepair>>()));
        services.AddHostedService<AgentGrantRepairService>();
        return services;
    }
}

/// <summary>Start hook that repairs missing default policy grants once when the host starts.</summary>
public sealed partial class AgentGrantRepairService(AgentGrantRepair repair, ILogger<AgentGrantRepairService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var repaired = await repair.RepairAsync(cancellationToken).ConfigureAwait(false);
        if (repaired > 0)
            LogRepaired(logger, repaired);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, EventId = 1, Message = "Repaired {Count} default policy grant(s) at start.")]
    private static partial void LogRepaired(ILogger logger, int count);
}
