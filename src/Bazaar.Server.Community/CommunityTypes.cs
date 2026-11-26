using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Community;

/// <summary>Verification state of a platform's policy record (doc 36, "Data and interfaces"). Only Verified allows posting.</summary>
public enum PolicyStatus
{
    /// <summary>Primary text was read and the record is current.</summary>
    Verified,

    /// <summary>Only a secondary source (search snippet, news) was available. Resolves to Deny for posting.</summary>
    Secondary,

    /// <summary>The rule was not read. Resolves to Deny for posting.</summary>
    Unverified,

    /// <summary>The platform bans agent or AI posting. Resolves to Deny at every tier.</summary>
    Banned,
}

/// <summary>Outward actions an agent may take on a community platform (doc 36, CommunityAction).</summary>
public enum CommunityAction
{
    Read,
    ReplyApprovedThread,
    NewPost,
    NewIssue,
    NewPullRequest,
    EditOwn,
    WithdrawOwn,
    CreateAccount,
    OutreachEmail,
}

/// <summary>One row of the platform policy matrix (doc 36).</summary>
public sealed record PlatformPolicy(
    string Platform,
    PolicyStatus Status,
    IReadOnlySet<CommunityAction> Allowed,
    string DisclosureRule,
    Uri SourceUrl,
    DateOnly CheckedOn);

/// <summary>Fixed community quota values from doc 36 and doc 15 (the shared table owns the public-item keys).</summary>
public static class CommunityQuotas
{
    /// <summary>security.quota.public_items_per_platform_day (doc 15 shared table).</summary>
    public static readonly RateRule PublicItemsPerPlatformDay = new(5, TimeSpan.FromDays(1));

    /// <summary>security.quota.public_issue_pr_per_repo_day (doc 15, also doc 21).</summary>
    public static readonly RateRule PublicIssuePrPerRepoDay = new(1, TimeSpan.FromDays(1));

    /// <summary>community.quota.new_repos_per_week (doc 36 settings).</summary>
    public static readonly RateRule NewReposPerWeek = new(3, TimeSpan.FromDays(7));
}
