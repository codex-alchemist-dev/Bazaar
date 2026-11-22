namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Active sessions per agent, held in process memory (doc 15, "Reads for modules"). The session runner calls
/// <see cref="Begin"/> when a session starts and <see cref="End"/> when it ends. Contents are lost on restart, which is safe:
/// sessions do not survive a restart either.
/// </summary>
/// <remarks>Thread-safe: every call takes one lock. Agent ids match exactly (ordinal).</remarks>
public sealed class InMemoryAgentSessionDirectory : IAgentSessionDirectory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<Guid>> _sessions = new(StringComparer.Ordinal);

    /// <summary>Marks a session as active for the agent. Adding the same session twice does nothing.</summary>
    public void Begin(string agentId, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session id must not be empty.", nameof(sessionId));
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(agentId, out var set))
            {
                set = [];
                _sessions[agentId] = set;
            }

            set.Add(sessionId);
        }
    }

    /// <summary>Removes a session from the agent's active set. Removing an unknown session does nothing.</summary>
    public void End(string agentId, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        lock (_gate)
        {
            if (_sessions.TryGetValue(agentId, out var set) && set.Remove(sessionId) && set.Count == 0)
            {
                _sessions.Remove(agentId);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Guid>> ListActiveAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<Guid> active = _sessions.TryGetValue(agentId, out var set) ? [.. set] : [];
            return ValueTask.FromResult(active);
        }
    }
}
