using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Policy;

public class RateLimitTests
{
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    private static readonly ToolCapability _search = new(
        "search.web",
        ToolTier.Autonomous,
        CapabilityClass.Read,
        SessionRoles.Actor,
        new RateRule(2, _window));

    private static readonly ToolCapability _push = new(
        "repo.push.quota",
        ToolTier.OwnerApproves,
        CapabilityClass.Push,
        SessionRoles.Actor,
        new RateRule(1, _window));

    private static ToolPolicyEngine Engine(IRateBucketStore store) =>
        new(CapabilityRegistry.Create([_search, _push]), store, PolicyOptions.Default);

    /// <summary>Records every quota touch and always allows, so tests can see whether the store was consulted.</summary>
    private sealed class CountingRateStore : IRateBucketStore
    {
        public List<string> Keys { get; } = [];

        public RateCheck TryConsume(string key, RateRule rule, DateTimeOffset now)
        {
            Keys.Add(key);
            return new RateCheck(true, 1);
        }
    }

    [Fact]
    public void A_rate_rule_allows_its_limit_then_denies_within_the_window()
    {
        var engine = Engine(new InMemoryRateBucketStore());

        var first = engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now));
        var second = engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now.AddSeconds(10)));
        var third = engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now.AddSeconds(30)));

        Assert.Equal(PolicyOutcome.Allow, first.Outcome);
        Assert.Equal(PolicyOutcome.Allow, second.Outcome);
        Assert.Equal(PolicyOutcome.Deny, third.Outcome);
        Assert.Equal(PolicyReason.RateLimited, third.Reason);
    }

    [Fact]
    public void A_rate_rule_allows_again_once_the_window_has_passed()
    {
        var engine = Engine(new InMemoryRateBucketStore());
        engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now));
        engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now.AddSeconds(10)));
        Assert.Equal(PolicyReason.RateLimited, engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now.AddSeconds(30))).Reason);

        var afterWindow = engine.Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now.AddMinutes(1)));

        Assert.Equal(PolicyOutcome.Allow, afterWindow.Outcome);
        Assert.Equal(PolicyReason.Autonomous, afterWindow.Reason);
    }

    [Fact]
    public void Quota_is_per_agent_so_one_agent_does_not_use_up_another()
    {
        var engine = Engine(new InMemoryRateBucketStore());
        engine.Evaluate(PolicyFixtures.Context("search.web", agent: "agent-a", now: PolicyFixtures._now));
        engine.Evaluate(PolicyFixtures.Context("search.web", agent: "agent-a", now: PolicyFixtures._now));
        Assert.Equal(
            PolicyReason.RateLimited,
            engine.Evaluate(PolicyFixtures.Context("search.web", agent: "agent-a", now: PolicyFixtures._now)).Reason);

        var agentB = engine.Evaluate(PolicyFixtures.Context("search.web", agent: "agent-b", now: PolicyFixtures._now));

        Assert.Equal(PolicyOutcome.Allow, agentB.Outcome);
    }

    [Fact]
    public void A_call_that_needs_approval_does_not_consume_quota()
    {
        var engine = Engine(new InMemoryRateBucketStore());

        var needsApproval = engine.Evaluate(PolicyFixtures.Context("repo.push.quota", now: PolicyFixtures._now));

        Assert.Equal(PolicyOutcome.RequireApproval, needsApproval.Outcome);

        // The quota allows one call. If the approval-required call had counted, this granted call would be refused.
        var grant = new VerifiedApproval(
            Guid.Parse("0196a6f0-0000-7000-8000-000000000302"),
            "agent-a",
            "repo.push.quota",
            DispatchHash.Compute("repo.push.quota", null, PolicyFixtures.Args("{}"), SessionTaint.Clean.Sources),
            PolicyFixtures._now.AddMinutes(10));

        var granted = engine.Evaluate(PolicyFixtures.Context("repo.push.quota", now: PolicyFixtures._now, grant: grant));

        Assert.Equal(PolicyOutcome.Allow, granted.Outcome);
        Assert.Equal(PolicyReason.ApprovedByGrant, granted.Reason);
    }

    [Fact]
    public void An_allowed_tool_without_a_rate_rule_does_not_touch_the_store()
    {
        var store = new CountingRateStore();
        var engine = new ToolPolicyEngine(
            CapabilityRegistry.Create([PolicyFixtures._read]),
            store,
            PolicyOptions.Default);

        var decision = engine.Evaluate(PolicyFixtures.Context("gc.read", now: PolicyFixtures._now));

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public void An_allowed_tool_with_a_rate_rule_touches_the_store_once()
    {
        var store = new CountingRateStore();

        Engine(store).Evaluate(PolicyFixtures.Context("search.web", now: PolicyFixtures._now));

        Assert.Single(store.Keys);
    }

    [Fact]
    public void A_role_denied_call_to_a_rate_limited_tool_does_not_touch_the_store()
    {
        var store = new CountingRateStore();

        var decision = Engine(store).Evaluate(PolicyFixtures.Context("search.web", role: SessionRoles.Reader, now: PolicyFixtures._now));

        Assert.Equal(PolicyReason.RoleNotPermitted, decision.Reason);
        Assert.Empty(store.Keys);
    }
}
