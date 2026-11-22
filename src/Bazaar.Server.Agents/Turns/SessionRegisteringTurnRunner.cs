using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Keeps the agent session directory true for the length of each run (doc 15, "Reads for modules"): it begins the request's
/// session before the inner <see cref="ITurnRunner"/> starts and ends it in a <c>finally</c>, so the session is removed however
/// the run ends (a result, an exception or cancellation). <see cref="IAgentSessionTaintReader"/> then sees exactly the sessions
/// that are running.
/// </summary>
/// <remarks>
/// The agent id is registered as <see cref="Guid.ToString(string)"/> with format <c>D</c>, the same form the tool dispatcher puts in
/// <c>DispatchContext.AgentId</c>. A request with an empty session id is refused before the inner runner starts (the registrar
/// throws), because its taint could not be read. A subagent child in its parent's session begins it a second time; the
/// registrar counts begins, so the session stays active until both runs have ended.
/// </remarks>
/// <param name="inner">The runner that does the work.</param>
/// <param name="sessions">Where the run's session is registered.</param>
public sealed class SessionRegisteringTurnRunner(ITurnRunner inner, IAgentSessionRegistrar sessions) : ITurnRunner
{
    private readonly ITurnRunner _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IAgentSessionRegistrar _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public async Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agentId = request.AgentId.ToString("D");
        _sessions.BeginSession(agentId, request.SessionId);
        try
        {
            return await _inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessions.EndSession(agentId, request.SessionId);
        }
    }
}
