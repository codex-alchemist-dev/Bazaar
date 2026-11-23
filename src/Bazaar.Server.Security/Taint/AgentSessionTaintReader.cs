namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Worst taint across an agent's active sessions (doc 15, "Reads for modules"), built on the per-session
/// <see cref="ISessionTaintReader"/> (in agent-turns, <c>ISessionTaintStore</c>) and an <see cref="IAgentSessionDirectory"/>.
/// </summary>
/// <remarks>
/// Fails closed: a directory or session read that throws is not caught, so the caller refuses. The reader stops early once
/// it sees <see cref="TaintState.Hard"/>, the highest level. Sessions are read one at a time.
/// </remarks>
public sealed class AgentSessionTaintReader(IAgentSessionDirectory directory, ISessionTaintReader sessions) : IAgentSessionTaintReader
{
    private readonly IAgentSessionDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly ISessionTaintReader _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public async ValueTask<TaintState> GetActiveTaintAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var active = await _directory.ListActiveAsync(agentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The session directory returned no list.");

        var worst = TaintState.Clean;
        foreach (var sessionId in active)
        {
            var taint = await _sessions.LoadAsync(sessionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session taint reader returned no taint.");

            if (!Enum.IsDefined(taint.State))
            {
                // An unknown level is treated as the highest one.
                return TaintState.Hard;
            }

            if (taint.State > worst)
            {
                worst = taint.State;
            }

            if (worst == TaintState.Hard)
            {
                break;
            }
        }

        return worst;
    }
}
