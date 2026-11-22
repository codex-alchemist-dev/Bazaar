namespace Bazaar.Server.Agents.Data;

/// <summary>An agent as a read-only listing shows it: the id, the display name the optional free-text role and the optional short model label such as "Haiku 5.5" (each null when unset).</summary>
public sealed record AgentListing(Guid AgentId, string Name, string? Role = null, string? Model = null);

/// <summary>Read-only list of the agents that are not retired, for views that only need names (for example the Org pane).</summary>
public interface IAgentDirectoryReader
{
    /// <summary>Lists agents in every state except retired, ordered by name then id.</summary>
    Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default);
}
