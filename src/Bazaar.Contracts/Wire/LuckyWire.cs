namespace Bazaar.Contracts.Wire;

/// <summary><c>lucky.start</c>: the owner starts a timed brainstorm (docs/design/26-fun-systems.md, section 6).
/// The server picks the candidates; the client sends only the project and the prompt.</summary>
/// <param name="ProjectId">The project the run belongs to.</param>
/// <param name="Prompt">The owner's prompt.</param>
public sealed record LuckyStartRequest(Guid ProjectId, string Prompt);

/// <summary>The reply to <c>lucky.start</c>.</summary>
/// <param name="RunId">The new run.</param>
/// <param name="GcId">The ephemeral brainstorm GC.</param>
/// <param name="Participants">Agents invited to the run.</param>
/// <param name="WakesInserted">Brainstorm wakes written for opted-in agents.</param>
public sealed record LuckyStartReply(string RunId, Guid GcId, int Participants, int WakesInserted);

/// <summary><c>lucky.idea</c>: a participating agent records one idea during the run.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Text">The idea text.</param>
public sealed record LuckyIdeaRequest(string RunId, string Text);

/// <summary>The reply to <c>lucky.idea</c>.</summary>
/// <param name="RunId">The run.</param>
/// <param name="IdeaIndex">The idea's index in the run.</param>
/// <param name="Quarantined">True when a secret-shaped string quarantined the idea.</param>
public sealed record LuckyIdeaReply(string RunId, int IdeaIndex, bool Quarantined);

/// <summary><c>lucky.mark</c>: the owner marks a kept idea for a Backlog card.</summary>
/// <param name="RunId">The run.</param>
/// <param name="IdeaIndex">The idea's index in the run.</param>
public sealed record LuckyMarkRequest(string RunId, int IdeaIndex);

/// <summary>The reply to <c>lucky.mark</c>.</summary>
/// <param name="RunId">The run.</param>
/// <param name="IdeaIndex">The idea's index in the run.</param>
/// <param name="CardId">The Backlog card for the idea.</param>
public sealed record LuckyMarkReply(string RunId, int IdeaIndex, Guid CardId);

/// <summary><c>lucky.opt_in</c>: the owner sets whether an agent takes part in Lucky runs.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="OptedIn">True to opt in.</param>
public sealed record LuckyOptInRequest(Guid AgentId, bool OptedIn);

/// <summary>The reply to <c>lucky.opt_in</c>.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="OptedIn">The stored setting.</param>
public sealed record LuckyOptInReply(Guid AgentId, bool OptedIn);
