using Bazaar.Server.Community.Policy;

namespace Bazaar.Server.Community.Tests.Policy;

public class PolicyResolverTests
{
    private static readonly DateOnly _today = new(2026, 10, 30);

    private static PlatformPolicy Record(string platform, PolicyStatus status, DateOnly? checkedOn = null, params CommunityAction[] allowed) =>
        new(platform, status, new HashSet<CommunityAction>(allowed), "test", new Uri("https://example.invalid/test"), checkedOn ?? new DateOnly(2026, 10, 24));

    private static PlatformPolicy Seeded(string platform) =>
        PlatformPolicySeed.All.Single(p => p.Platform == platform);

    [Fact]
    public void Unknown_platform_is_denied_for_every_action()
    {
        foreach (var action in Enum.GetValues<CommunityAction>())
        {
            var verdict = PolicyResolver.Decide(null, action, _today);

            Assert.False(verdict.IsAllowed);
            Assert.Equal(PolicyOutcome.Deny, verdict.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        }
    }

    [Fact]
    public void Hacker_news_is_denied_for_every_action_including_read()
    {
        var hn = Seeded("Hacker News");

        foreach (var action in Enum.GetValues<CommunityAction>())
        {
            Assert.False(PolicyResolver.Decide(hn, action, _today).IsAllowed);
        }
    }

    [Fact]
    public void Banned_record_is_denied_even_when_it_lists_actions()
    {
        var banned = Record("Test banned", PolicyStatus.Banned, null, CommunityAction.Read, CommunityAction.NewIssue);

        Assert.False(PolicyResolver.Decide(banned, CommunityAction.Read, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(banned, CommunityAction.NewIssue, _today).IsAllowed);
    }

    [Fact]
    public void Unverified_platform_is_denied_for_every_action_including_read()
    {
        foreach (var platform in new[] { "Stack Overflow", "Discord", "Roblox DevForum", "Godot" })
        {
            var policy = Seeded(platform);
            Assert.Equal(PolicyStatus.Unverified, policy.Status);

            foreach (var action in Enum.GetValues<CommunityAction>())
            {
                Assert.False(PolicyResolver.Decide(policy, action, _today).IsAllowed);
            }
        }
    }

    [Fact]
    public void Unverified_record_listing_read_is_still_denied()
    {
        var unverified = Record("Test unverified", PolicyStatus.Unverified, null, CommunityAction.Read);

        Assert.False(PolicyResolver.Decide(unverified, CommunityAction.Read, _today).IsAllowed);
    }

    [Fact]
    public void Secondary_record_allows_read_only()
    {
        var reddit = Seeded("Reddit");

        Assert.True(PolicyResolver.Decide(reddit, CommunityAction.Read, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(reddit, CommunityAction.NewIssue, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(reddit, CommunityAction.ReplyApprovedThread, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(reddit, CommunityAction.CreateAccount, _today).IsAllowed);
    }

    [Fact]
    public void Verified_github_allows_new_issue_and_denies_create_account()
    {
        var github = Seeded("GitHub");

        Assert.True(PolicyResolver.Decide(github, CommunityAction.NewIssue, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(github, CommunityAction.CreateAccount, _today).IsAllowed);
    }

    [Fact]
    public void Verified_github_denies_outreach_email_and_new_post()
    {
        var github = Seeded("GitHub");

        Assert.False(PolicyResolver.Decide(github, CommunityAction.OutreachEmail, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(github, CommunityAction.NewPost, _today).IsAllowed);
    }

    [Fact]
    public void Verified_record_allows_only_actions_in_its_allowed_set()
    {
        var verified = Record("Test verified", PolicyStatus.Verified, null, CommunityAction.Read, CommunityAction.EditOwn);

        Assert.True(PolicyResolver.Decide(verified, CommunityAction.EditOwn, _today).IsAllowed);
        Assert.False(PolicyResolver.Decide(verified, CommunityAction.WithdrawOwn, _today).IsAllowed);
    }

    [Fact]
    public void Stale_verified_record_is_denied()
    {
        var github = Seeded("GitHub");
        var stale = new DateOnly(2026, 11, 24);

        Assert.False(PolicyResolver.Decide(github, CommunityAction.NewIssue, stale).IsAllowed);
        Assert.False(PolicyResolver.Decide(github, CommunityAction.Read, stale).IsAllowed);
    }

    [Fact]
    public void Record_exactly_thirty_days_old_is_still_allowed()
    {
        var github = Seeded("GitHub");
        var edge = new DateOnly(2026, 11, 23);

        Assert.True(PolicyResolver.Decide(github, CommunityAction.NewIssue, edge).IsAllowed);
    }

    [Fact]
    public void Stale_secondary_record_is_denied_for_read()
    {
        var reddit = Seeded("Reddit");

        Assert.False(PolicyResolver.Decide(reddit, CommunityAction.Read, new DateOnly(2026, 11, 24)).IsAllowed);
    }

    [Fact]
    public void Denied_verdicts_carry_a_reason()
    {
        var verdict = PolicyResolver.Decide(Seeded("GitHub"), CommunityAction.CreateAccount, _today);

        Assert.Equal(PolicyOutcome.Deny, verdict.Outcome);
        Assert.Contains("CreateAccount", verdict.Reason, StringComparison.Ordinal);
    }
}
