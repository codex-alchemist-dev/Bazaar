namespace Bazaar.Contracts.Wire;

/// <summary>One agent the owner's client offers to a Lucky run.</summary>
/// <param name="AgentId">The agent's id, as a GUID string.</param>
/// <param name="OnShift">True when the agent is on shift now.</param>
/// <param name="OptedIn">True when the agent opted in to Lucky runs.</param>
public sealed record LuckyStartCandidate(string AgentId, bool OnShift, bool OptedIn);

/// <summary><c>lucky.start</c>: the owner starts a timed brainstorm (docs/design/26-fun-systems.md, section 6).</summary>
/// <param name="ProjectId">The project the run belongs to.</param>
/// <param name="Prompt">The owner's prompt.</param>
/// <param name="Candidates">The agents the run may invite. The planner keeps only those the setting allows.</param>
public sealed record LuckyStartRequest(Guid ProjectId, string Prompt, IReadOnlyList<LuckyStartCandidate> Candidates);

/// <summary>The reply to <c>lucky.start</c>.</summary>
/// <param name="RunId">The new run.</param>
/// <param name="GcId">The ephemeral brainstorm GC.</param>
/// <param name="Participants">Agents invited to the run.</param>
/// <param name="WakesInserted">Brainstorm wakes written for opted-in agents.</param>
public sealed record LuckyStartReply(string RunId, Guid GcId, int Participants, int WakesInserted);
