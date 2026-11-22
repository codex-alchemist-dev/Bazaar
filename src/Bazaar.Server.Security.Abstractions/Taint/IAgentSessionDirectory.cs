namespace Bazaar.Server.Security.Taint;

/// <summary>
/// The sessions an agent has open now (doc 15, "Reads for modules"). The session runner adds a session when it starts and
/// removes it when it ends; readers only list.
/// </summary>
public interface IAgentSessionDirectory
{
    /// <summary>Lists the agent's active session ids. Empty when the agent has none. Throws when the directory cannot answer.</summary>
    /// <param name="agentId">The agent. Must not be blank.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<IReadOnlyList<Guid>> ListActiveAsync(string agentId, CancellationToken ct = default);
}
