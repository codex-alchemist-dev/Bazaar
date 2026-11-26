using Bazaar.Server.CompanyKnowledge.Abstractions;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>
/// Operator options for the company tier (docs/design/08-memory-system.md, 6.3 to 6.7; settings <c>company_knowledge.*</c>). Safe shipped
/// defaults; every value may only tighten what the doc fixes.
/// </summary>
public sealed record CompanyKnowledgeOptions
{
    /// <summary><c>company_knowledge.review_days</c>: days a T2 candidate waits before the window admits it. 0 means only the owner admits.</summary>
    public int ReviewDays { get; init; } = 14;

    /// <summary><c>company_knowledge.admission</c>: the weakest authority that may admit a T2 candidate. May tighten, never loosen.</summary>
    public CompanyAdmissionAuthority MinAuthority { get; init; } = CompanyAdmissionAuthority.ReviewWindow;

    /// <summary><c>company_knowledge.confirmed_min_projects</c>.</summary>
    public int ConfirmedMinProjects { get; init; } = 2;

    /// <summary><c>company_knowledge.review_interval_days</c> for T2.</summary>
    public int ReviewIntervalDaysT2 { get; init; } = 180;

    /// <summary><c>company_knowledge.review_interval_days</c> for T3.</summary>
    public int ReviewIntervalDaysT3 { get; init; } = 365;

    /// <summary><c>company_knowledge.max_proposals_per_agent_week</c>.</summary>
    public int MaxProposalsPerAgentWeek { get; init; } = 10;

    /// <summary>The agent id that authors the commits this module makes on admission and rejection.</summary>
    public string WriterAgentId { get; init; } = "company-knowledge";
}
