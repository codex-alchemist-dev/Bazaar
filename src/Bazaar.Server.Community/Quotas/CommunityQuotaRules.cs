using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Community.Quotas;

/// <summary>Maps community actions to the quota rules they consume (doc 36, "Rate limits, audit and takedown").</summary>
public static class CommunityQuotaRules
{
    /// <summary>The platform-wide "new public items" rule for an action, or null when the action has no such quota.</summary>
    public static RateRule? RuleFor(CommunityAction action) => action switch
    {
        CommunityAction.NewPost or CommunityAction.NewIssue or CommunityAction.NewPullRequest
            => CommunityQuotas.PublicItemsPerPlatformDay,
        _ => null,
    };

    /// <summary>The per-repository rule for an action, or null when the action has no repository quota.</summary>
    public static RateRule? RuleForRepo(CommunityAction action) => action switch
    {
        CommunityAction.NewIssue or CommunityAction.NewPullRequest => CommunityQuotas.PublicIssuePrPerRepoDay,
        _ => null,
    };

    /// <summary>The per-agent weekly rule for creating a new repository.</summary>
    public static RateRule RuleForNewRepo() => CommunityQuotas.NewReposPerWeek;
}
