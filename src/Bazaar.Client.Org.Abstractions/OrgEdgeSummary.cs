namespace Bazaar.Client.Org;

/// <summary>One Org edge between two agents. <see cref="ProjectId"/> is null for a company-wide edge; <see cref="EndsAt"/> is null while open-ended.</summary>
public sealed record OrgEdgeSummary(
    string Id,
    OrgEdgeKind Kind,
    string FromAgentId,
    string ToAgentId,
    string? ProjectId,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);
