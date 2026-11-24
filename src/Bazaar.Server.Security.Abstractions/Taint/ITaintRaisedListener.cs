namespace Bazaar.Server.Security.Taint;

/// <summary>
/// A session's taint level rose (doc 15, "Taint raised event"). Raised by the server's own tracker after the new taint is saved,
/// never from message content. <paramref name="AgentId"/> is the agent id in its <c>"D"</c> form.
/// </summary>
/// <param name="AgentId">The agent whose session was tainted.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Level">The new level; always above the previous one.</param>
/// <param name="At">When the tracker saw the rise.</param>
public sealed record TaintRaised(string AgentId, Guid SessionId, TaintState Level, DateTimeOffset At);

/// <summary>
/// Called in process when a session's taint level rises, once per rise and never on a repeat at the same level. Keep it quick:
/// the dispatcher awaits listeners before it returns the tool result. A listener that throws is logged and skipped.
/// </summary>
public interface ITaintRaisedListener
{
    Task OnTaintRaisedAsync(TaintRaised raised, CancellationToken cancellationToken = default);
}
