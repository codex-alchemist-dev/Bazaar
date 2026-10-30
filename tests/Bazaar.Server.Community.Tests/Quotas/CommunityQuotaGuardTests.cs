using Bazaar.Server.Community.Quotas;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Community.Tests.Quotas;

public class CommunityQuotaGuardTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static CommunityQuotaGuard NewGuard(InMemoryRateBucketStore store, InMemoryCommunityFreezeStore freezes) =>
        new(store, freezes);

    [Fact]
    public void Five_public_items_per_platform_per_day_then_denied()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());

        for (var i = 0; i < 5; i++)
        {
            Assert.True(guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start).Allowed);
        }

        var sixth = guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start);
        Assert.False(sixth.Allowed);
        Assert.Equal(CommunityQuotaKeys.PublicItemsScope, sixth.ExhaustedScope);
    }

    [Fact]
    public void Public_items_window_resets_after_a_day()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());
        for (var i = 0; i < 5; i++)
        {
            guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start);
        }

        // Breach freezes the pair; the owner clears it before the next day.
        guard.Unfreeze("agent-1", "reddit");
        Assert.True(guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start.AddDays(1)).Allowed);
    }

    [Fact]
    public void One_issue_per_repo_per_day()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());

        Assert.True(guard.Check("agent-1", "github", CommunityAction.NewIssue, "acme/tool", _start).Allowed);

        var second = guard.Check("agent-1", "github", CommunityAction.NewIssue, "acme/tool", _start);
        Assert.False(second.Allowed);
        Assert.Equal(CommunityQuotaKeys.RepoIssuePrScope, second.ExhaustedScope);
    }

    [Fact]
    public void A_different_repo_is_still_allowed()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());

        Assert.True(guard.Check("agent-1", "github", CommunityAction.NewIssue, "acme/tool", _start).Allowed);
        Assert.True(guard.Check("agent-1", "github", CommunityAction.NewPullRequest, "acme/other", _start).Allowed);
    }

    [Fact]
    public void Denied_repo_check_does_not_consume_the_platform_bucket()
    {
        var store = new InMemoryRateBucketStore();
        var guard = NewGuard(store, new InMemoryCommunityFreezeStore());

        Assert.True(guard.Check("agent-1", "github", CommunityAction.NewIssue, "acme/tool", _start).Allowed);
        Assert.False(guard.Check("agent-1", "github", CommunityAction.NewIssue, "acme/tool", _start).Allowed);

        // Probe the platform bucket directly: one item was counted by the first issue, and the probe is the second.
        var probe = store.TryConsume(
            CommunityQuotaKeys.Key("agent-1", "github", CommunityQuotaKeys.PublicItemsScope),
            CommunityQuotas.PublicItemsPerPlatformDay,
            _start);
        Assert.True(probe.Allowed);
        Assert.Equal(2, probe.Count);
    }

    [Fact]
    public void Breach_freezes_the_pair_and_unfreeze_clears_it()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());
        for (var i = 0; i < 5; i++)
        {
            guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start);
        }

        Assert.False(guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start).Allowed);
        Assert.True(guard.IsFrozen("agent-1", "reddit"));

        var whileFrozen = guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start.AddDays(1));
        Assert.False(whileFrozen.Allowed);
        Assert.Equal(CommunityQuotaGuard.FrozenScope, whileFrozen.ExhaustedScope);

        guard.Unfreeze("agent-1", "reddit");
        Assert.False(guard.IsFrozen("agent-1", "reddit"));
        Assert.True(guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start.AddDays(1)).Allowed);
    }

    [Fact]
    public void Freeze_is_scoped_to_the_agent_and_platform()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());
        for (var i = 0; i < 6; i++)
        {
            guard.Check("agent-1", "reddit", CommunityAction.NewPost, null, _start);
        }

        Assert.False(guard.IsFrozen("agent-1", "github"));
        Assert.False(guard.IsFrozen("agent-2", "reddit"));
    }

    [Fact]
    public void New_repo_quota_is_three_per_week()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());

        for (var i = 0; i < 3; i++)
        {
            Assert.True(guard.CheckNewRepo("agent-1", "github", _start.AddDays(i)).Allowed);
        }

        var fourth = guard.CheckNewRepo("agent-1", "github", _start.AddDays(3));
        Assert.False(fourth.Allowed);
        Assert.Equal(CommunityQuotaKeys.NewReposScope, fourth.ExhaustedScope);
    }

    [Fact]
    public void Repo_action_without_a_repo_reference_is_rejected()
    {
        var guard = NewGuard(new InMemoryRateBucketStore(), new InMemoryCommunityFreezeStore());

        Assert.Throws<ArgumentException>(() => guard.Check("agent-1", "github", CommunityAction.NewIssue, null, _start));
    }

    [Theory]
    [InlineData("a:b", "github")]
    [InlineData("", "github")]
    [InlineData("agent-1", "")]
    [InlineData("agent-1", "Reddit")]
    [InlineData("agent-1", "git:hub")]
    public void Key_validation_rejects_colons_empty_parts_and_upper_case_platforms(string agentId, string platform)
    {
        Assert.Throws<ArgumentException>(() =>
            CommunityQuotaKeys.Key(agentId, platform, CommunityQuotaKeys.PublicItemsScope));
        Assert.Throws<ArgumentException>(() =>
            CommunityQuotaKeys.RepoKey(agentId, platform, "acme/tool", CommunityQuotaKeys.RepoIssuePrScope));
    }

    [Fact]
    public void Key_validation_rejects_colons_in_the_repo_reference_and_unknown_scopes()
    {
        Assert.Throws<ArgumentException>(() =>
            CommunityQuotaKeys.RepoKey("agent-1", "github", "acme:tool", CommunityQuotaKeys.RepoIssuePrScope));
        Assert.Throws<ArgumentException>(() => CommunityQuotaKeys.Key("agent-1", "github", "unknown"));
    }

    [Fact]
    public void Keys_are_distinct_per_agent_platform_and_repo()
    {
        var a = CommunityQuotaKeys.RepoKey("agent-1", "github", "acme/tool", CommunityQuotaKeys.RepoIssuePrScope);
        var b = CommunityQuotaKeys.RepoKey("agent-1", "github", "acme/other", CommunityQuotaKeys.RepoIssuePrScope);
        var c = CommunityQuotaKeys.RepoKey("agent-2", "github", "acme/tool", CommunityQuotaKeys.RepoIssuePrScope);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Rules_return_the_doc_values_per_action()
    {
        Assert.Same(CommunityQuotas.PublicItemsPerPlatformDay, CommunityQuotaRules.RuleFor(CommunityAction.NewPost));
        Assert.Same(CommunityQuotas.PublicItemsPerPlatformDay, CommunityQuotaRules.RuleFor(CommunityAction.NewIssue));
        Assert.Same(CommunityQuotas.PublicItemsPerPlatformDay, CommunityQuotaRules.RuleFor(CommunityAction.NewPullRequest));
        Assert.Null(CommunityQuotaRules.RuleFor(CommunityAction.Read));

        Assert.Same(CommunityQuotas.PublicIssuePrPerRepoDay, CommunityQuotaRules.RuleForRepo(CommunityAction.NewIssue));
        Assert.Same(CommunityQuotas.PublicIssuePrPerRepoDay, CommunityQuotaRules.RuleForRepo(CommunityAction.NewPullRequest));
        Assert.Null(CommunityQuotaRules.RuleForRepo(CommunityAction.NewPost));

        Assert.Same(CommunityQuotas.NewReposPerWeek, CommunityQuotaRules.RuleForNewRepo());
        Assert.Equal(5, CommunityQuotaRules.RuleFor(CommunityAction.NewPost)!.Limit);
        Assert.Equal(1, CommunityQuotaRules.RuleForRepo(CommunityAction.NewIssue)!.Limit);
        Assert.Equal(TimeSpan.FromDays(7), CommunityQuotaRules.RuleForNewRepo().Window);
    }
}
