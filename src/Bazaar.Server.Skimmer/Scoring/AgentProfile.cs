namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>
/// An agent the message is scored for. <paramref name="Subscriptions"/> are opaque strings (a GC, tag, path or
/// Kanban card); the scorer does not interpret them.
/// </summary>
public sealed record AgentProfile(Guid AgentId, string Name, IReadOnlyList<string> Subscriptions);
