namespace Bazaar.Server.Community.Policy;

/// <summary>
/// The platform policy matrix from doc 36 ("Platform policy matrix", checked 2026-10-24), as code. It is the fallback
/// when the <c>platform_policy</c> table is empty and the reference the seed tests check against. Platform names are the
/// matrix row labels. Any platform not listed here is unknown and resolves to Deny.
/// </summary>
public static class PlatformPolicySeed
{
    private static readonly DateOnly _checkedOn = new(2026, 10, 24);

    /// <summary>Source for rows with no URL in the doc. A placeholder so the NOT NULL column holds an obviously unverified value.</summary>
    private static readonly Uri _unverifiedSource = new("https://example.invalid/unverified");

    /// <summary>The seeded policy records, in matrix order.</summary>
    public static IReadOnlyList<PlatformPolicy> All { get; } =
    [
        // Banned: "No generated or AI-edited text; no automated posting". Deny at every tier, including Read.
        new PlatformPolicy(
            "Hacker News",
            PolicyStatus.Banned,
            new HashSet<CommunityAction>(),
            "n/a",
            new Uri("https://news.ycombinator.com/newsguidelines.html"),
            _checkedOn),

        // Verified summary: apps allowed, bot-made accounts barred. Humans create the account (CreateAccount is not allowed).
        // The doc's Sources line names "GitHub ToS and AUP" with no URL; the docs.github.com ToS page is used here and must be confirmed.
        new PlatformPolicy(
            "GitHub",
            PolicyStatus.Verified,
            new HashSet<CommunityAction>
            {
                CommunityAction.Read,
                CommunityAction.ReplyApprovedThread,
                CommunityAction.NewIssue,
                CommunityAction.NewPullRequest,
                CommunityAction.EditOwn,
                CommunityAction.WithdrawOwn,
            },
            "App identity",
            new Uri("https://docs.github.com/en/site-policy/github-terms/github-terms-of-service"),
            _checkedOn),

        new PlatformPolicy(
            "Stack Overflow",
            PolicyStatus.Unverified,
            new HashSet<CommunityAction> { CommunityAction.Read },
            "Unknown",
            _unverifiedSource,
            _checkedOn),

        // Secondary: only an end date was found (API access reportedly ends March 2027). Read only.
        new PlatformPolicy(
            "Reddit",
            PolicyStatus.Secondary,
            new HashSet<CommunityAction> { CommunityAction.Read },
            "Unknown",
            _unverifiedSource,
            _checkedOn),

        new PlatformPolicy(
            "Discord",
            PolicyStatus.Unverified,
            new HashSet<CommunityAction> { CommunityAction.Read },
            "Unknown",
            _unverifiedSource,
            _checkedOn),

        new PlatformPolicy(
            "Roblox DevForum",
            PolicyStatus.Unverified,
            new HashSet<CommunityAction> { CommunityAction.Read },
            "Unknown",
            _unverifiedSource,
            _checkedOn),

        // Split: CONTRIBUTING.md has no AI rule, a secondary report says June 2026 bans autonomous agents. Deny (doc 36 Conflicts 3).
        new PlatformPolicy(
            "Godot",
            PolicyStatus.Unverified,
            new HashSet<CommunityAction> { CommunityAction.Read },
            "Unknown",
            new Uri("https://github.com/godotengine/godot/blob/master/CONTRIBUTING.md"),
            _checkedOn),
    ];
}
