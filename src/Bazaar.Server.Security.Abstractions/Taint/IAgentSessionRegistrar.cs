namespace Bazaar.Server.Security.Taint;

/// <summary>
/// The write side of <see cref="IAgentSessionDirectory"/> (doc 15, "Reads for modules"). Only the session runner uses it: it calls
/// <see cref="BeginSession"/> when a run starts and <see cref="EndSession"/> in a <c>finally</c> when the run ends, however it ends.
/// </summary>
/// <remarks>
/// Begins are counted per session: a subagent child that runs in its parent's session begins it again, and the session stays
/// active until every run that began it has ended.
/// </remarks>
public interface IAgentSessionRegistrar
{
    /// <summary>Marks the session as active for the agent, or counts one more run in it.</summary>
    /// <param name="agentId">The agent. Must not be blank.</param>
    /// <param name="sessionId">The session. Must not be empty.</param>
    void BeginSession(string agentId, Guid sessionId);

    /// <summary>Ends one run in the session. The session stops being active when its last run ends. An unknown session does nothing.</summary>
    /// <param name="agentId">The agent. Must not be blank.</param>
    /// <param name="sessionId">The session.</param>
    void EndSession(string agentId, Guid sessionId);
}
