using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Data;

/// <summary>
/// Repairs default policy grants that failed after an agent was created (doc 45, section 1.15). A grant that throws leaves the
/// agent with no policy row, and the seeds then find the agent by its marker and never grant again. On start, the host calls
/// <see cref="RepairAsync"/>: it lists every agent id, asks <see cref="IAgentPolicyPresence"/> which of them have no policy row,
/// and grants those. One agent's failure is logged and does not stop the others.
/// </summary>
/// <param name="grant">The default policy grant. It must be idempotent.</param>
/// <param name="presence">Reports which agents have no policy row. Null means nothing is repaired.</param>
/// <param name="listAgentIds">Returns the id of every agent.</param>
/// <param name="logger">Receives a warning for each failed grant.</param>
public sealed partial class AgentGrantRepair(
    IAgentDefaultPolicyGrant grant,
    IAgentPolicyPresence? presence,
    Func<CancellationToken, Task<IReadOnlyList<Guid>>> listAgentIds,
    ILogger<AgentGrantRepair> logger)
{
    /// <summary>Grants the default policy to each agent that has no policy row. Returns how many grants succeeded.</summary>
    /// <remarks>Returns 0 when no <see cref="IAgentPolicyPresence"/> is registered.</remarks>
    public async Task<int> RepairAsync(CancellationToken ct)
    {
        if (presence is null)
            return 0;

        var allAgents = await listAgentIds(ct).ConfigureAwait(false);
        var agents = await presence.FilterWithoutPolicyAsync(allAgents, ct).ConfigureAwait(false);
        var repaired = 0;
        foreach (var agentId in agents)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await grant.GrantAsync(agentId, ct).ConfigureAwait(false);
                repaired++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogRepairFailed(logger, agentId, ex);
            }
        }

        return repaired;
    }

    [LoggerMessage(Level = LogLevel.Warning, EventId = 1, Message = "Default policy grant repair failed for agent {AgentId}; it will be retried on the next start.")]
    private static partial void LogRepairFailed(ILogger logger, Guid agentId, Exception exception);
}
