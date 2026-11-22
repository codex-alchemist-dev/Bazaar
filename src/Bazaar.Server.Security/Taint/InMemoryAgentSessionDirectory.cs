namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Active sessions per agent, held in process memory (doc 15, "Reads for modules"). The session runner calls
/// <see cref="BeginSession"/> when a run starts and <see cref="EndSession"/> when it ends. Contents are lost on restart, which is safe:
/// sessions do not survive a restart either.
/// </summary>
/// <remarks>
/// Thread-safe: every call takes one lock. Agent ids match exactly (ordinal). Begins are counted, so a session that two runs
/// share (a subagent child in its parent's session) stays listed until both have ended.
/// </remarks>
public sealed class InMemoryAgentSessionDirectory : IAgentSessionDirectory, IAgentSessionRegistrar
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<Guid, int>> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void BeginSession(string agentId, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session id must not be empty.", nameof(sessionId));
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(agentId, out var runs))
            {
                runs = [];
                _sessions[agentId] = runs;
            }

            runs[sessionId] = runs.GetValueOrDefault(sessionId) + 1;
        }
    }

    /// <inheritdoc />
    public void EndSession(string agentId, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(agentId, out var runs) || !runs.TryGetValue(sessionId, out var count))
            {
                return;
            }

            if (count > 1)
            {
                runs[sessionId] = count - 1;
                return;
            }

            runs.Remove(sessionId);
            if (runs.Count == 0)
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
            IReadOnlyList<Guid> active = _sessions.TryGetValue(agentId, out var runs) ? [.. runs.Keys] : [];
            return ValueTask.FromResult(active);
        }
    }
}
