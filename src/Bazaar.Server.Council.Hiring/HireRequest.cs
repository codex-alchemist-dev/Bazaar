namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// A request to hire a Talent Pool template (docs/design/25-company-systems-knowledge.md, Talent Pool).
/// </summary>
/// <param name="TemplateId">The Talent template id. The hire subject is this string, not an agent id: no agent exists before the hire.</param>
/// <param name="GcId">The GC the hire vote runs in. Its agent members form the electorate.</param>
/// <param name="ProposerId">The agent opening the vote. Cannot vote on it.</param>
/// <param name="SubjectAuthorId">The author of the template, excluded from voting when council.exclude_author is set. Null when unknown.</param>
/// <param name="Deadline">The requested deadline. The Council caps it.</param>
public sealed record HireRequest(
    string TemplateId,
    Guid GcId,
    string ProposerId,
    string? SubjectAuthorId,
    DateTimeOffset Deadline);
