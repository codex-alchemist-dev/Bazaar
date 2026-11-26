namespace Bazaar.Server.Reputation;

/// <summary>An agent's score on one tag. <c>EvidenceCount</c> is how many ledger items fed it.</summary>
public sealed record ScoreView(string AgentId, string Tag, ScoreResult Score, int EvidenceCount);

/// <summary>An agent suggested for a tag, ranked by the lower confidence bound.</summary>
public sealed record Suggestion(string AgentId, ScoreResult Score, int EvidenceCount);

/// <summary>Read side of reputation (doc 11). Scores are computed from the ledger on read with <see cref="ScoreMath"/>.</summary>
public interface IScoreProjection
{
    /// <summary>The agent's score on a tag. With no evidence the score is the prior, 0.5.</summary>
    Task<ScoreView> GetAsync(string agentId, string tag, CancellationToken cancellationToken);

    /// <summary>Agents with evidence on the tag, best lower confidence bound first.</summary>
    Task<IReadOnlyList<Suggestion>> SuggestAsync(string tag, int limit, CancellationToken cancellationToken);
}

/// <summary>Reads the evidence for one agent and tag, and the agents that have any for a tag.</summary>
public interface IEvidenceSource
{
    Task<IReadOnlyList<Evidence>> ReadAsync(string agentId, string tag, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> AgentsWithEvidenceAsync(string tag, CancellationToken cancellationToken);
}
