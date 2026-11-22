namespace Bazaar.Server.Agents.Data;

/// <summary>An agent as a read-only listing shows it: the id and the display name.</summary>
public sealed record AgentListing(Guid AgentId, string Name);

/// <summary>Read-only list of the agents that are not retired, for views that only need names (for example the Org pane).</summary>
public interface IAgentDirectoryReader
{
    /// <summary>Lists agents in every state except retired, ordered by name then id.</summary>
    Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default);
}
