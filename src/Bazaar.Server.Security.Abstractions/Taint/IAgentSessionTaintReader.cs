namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Read-only taint of an agent across its active sessions, for modules that know an agent but not its sessions (doc 15,
/// "Reads for modules").
/// </summary>
public interface IAgentSessionTaintReader
{
    /// <summary>
    /// The worst <see cref="TaintState"/> across the agent's active sessions; <see cref="TaintState.Clean"/> when it has none.
    /// Throws when a session or the directory cannot be read, so the caller fails closed.
    /// </summary>
    /// <param name="agentId">The agent. Must not be blank.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<TaintState> GetActiveTaintAsync(string agentId, CancellationToken ct = default);
}
