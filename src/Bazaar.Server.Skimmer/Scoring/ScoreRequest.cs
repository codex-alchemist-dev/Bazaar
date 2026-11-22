namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>One scoring call: a message and the agents it is scored for. The batched unit (doc 13, cost table).</summary>
public sealed record ScoreRequest(GcMessageRef Message, IReadOnlyList<AgentProfile> Agents);
