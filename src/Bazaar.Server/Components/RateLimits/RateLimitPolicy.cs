namespace Bazaar.Server.Components.RateLimits;

/// <summary>What one rate-limit bucket is keyed by. The key value itself is supplied at call time.</summary>
public enum RateLimitScope
{
    /// <summary>One bucket for the whole install.</summary>
    Installation,

    /// <summary>One bucket per agent.</summary>
    Agent,

    /// <summary>One bucket per community platform (for example, one per platform name).</summary>
    Platform,

    /// <summary>One bucket per repository.</summary>
    Repository,
}

/// <summary>
/// A quota: at most <see cref="Limit"/> admitted events per <see cref="Window"/>, counted per scope key.
/// Quotas, not money budgets (doc 15, "Shared quotas and limits (C36)"; Resource Economy is rejected).
/// </summary>
/// <param name="Name">Stable identifier. Buckets are isolated by policy name, so two policies never share counters.</param>
/// <param name="Limit">Maximum admitted events inside one window.</param>
/// <param name="Window">Sliding window length.</param>
/// <param name="Scope">What the bucket key means. The caller passes the matching key (agent id, platform name, and so on).</param>
/// <param name="SettingKey">The doc 15 Settings key that will override <see cref="Limit"/>. Not registered yet (see the
/// TODO on <see cref="RateLimitServiceCollectionExtensions"/>).</param>
public sealed record RateLimitPolicy(string Name, int Limit, TimeSpan Window, RateLimitScope Scope, string SettingKey);

/// <summary>
/// The shared defaults from the doc 15 quota table. Each value is a <c>[PROPOSED]</c> default: doc 15 marks the whole table
/// PROPOSED, and the approval-request row is tagged PROPOSED on its own line. Values here are the defaults only; the
/// setting keys are the override points. Subsystems that own their own limits (docs 05, 06, 13, 15, 21, 22, 36) reference
/// these rows rather than redefining them.
/// </summary>
public static class SharedRateLimitPolicies
{
    /// <summary>Doc 15 row "New public items (community posts) per platform", owned by doc 36: 5 per platform per day.</summary>
    public static readonly RateLimitPolicy PublicItemsPerPlatformDay = new(
        "public_items_per_platform_day", 5, TimeSpan.FromDays(1), RateLimitScope.Platform,
        "security.quota.public_items_per_platform_day");

    /// <summary>
    /// Doc 15 row "New issue or PR per repository", shared by docs 21 and 36: 1 per repository per day.
    /// </summary>
    public static readonly RateLimitPolicy PublicIssueOrPrPerRepositoryDay = new(
        "public_issue_pr_per_repo_day", 1, TimeSpan.FromDays(1), RateLimitScope.Repository,
        "security.quota.public_issue_pr_per_repo_day");

    /// <summary>Doc 15 row "New repositories", owned by doc 36: 3 per week, per install.</summary>
    public static readonly RateLimitPolicy NewRepositoriesPerWeek = new(
        "new_repos_per_week", 3, TimeSpan.FromDays(7), RateLimitScope.Installation,
        "security.quota.new_repos_per_week");

    /// <summary>Doc 15 row "Mail sent", owned by doc 22: 20 per day per agent.</summary>
    public static readonly RateLimitPolicy MailSentPerDay = new(
        "mail_sent_per_day", 20, TimeSpan.FromDays(1), RateLimitScope.Agent,
        "security.quota.mail_sent_per_day");

    /// <summary>Doc 15 row "GitHub content writes", owned by doc 21: 30 per minute per GitHub App installation.</summary>
    public static readonly RateLimitPolicy GitHubWritesPerMinute = new(
        "github_writes_per_minute", 30, TimeSpan.FromMinutes(1), RateLimitScope.Installation,
        "security.quota.github_writes_per_minute");

    /// <summary>
    /// Doc 15 reference row "Skimmer scored wakes", owned by doc 13 (which owns the key): 20 per agent per hour.
    /// Listed here only so the shared table is complete; doc 13 is the authority for this value.
    /// </summary>
    public static readonly RateLimitPolicy SkimmerScoredWakesPerHour = new(
        "skimmer_scored_wakes_per_hour", 20, TimeSpan.FromHours(1), RateLimitScope.Agent,
        "skimmer.scored_wakes_per_hour");

    /// <summary>
    /// Doc 15 row "Approval requests", owned by doc 15 (tagged [PROPOSED] on its own line): 10 per agent per hour.
    /// </summary>
    public static readonly RateLimitPolicy ApprovalRequestsPerAgentHour = new(
        "approval_requests_per_agent_hour", 10, TimeSpan.FromHours(1), RateLimitScope.Agent,
        "security.approval.request_cap_per_agent_hour");
}
