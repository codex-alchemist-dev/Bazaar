using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Mentions;

public class BroadcastPolicyTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static GcViewer Viewer(PrincipalKind kind) => new(Id(10), kind);

    [Theory]
    [InlineData(PrincipalKind.Owner, BroadcastPolicy.OwnerOnly, true, BroadcastDenial.None)]
    [InlineData(PrincipalKind.Owner, BroadcastPolicy.OwnerAndAgentsRateLimited, true, BroadcastDenial.None)]
    [InlineData(PrincipalKind.Human, BroadcastPolicy.OwnerOnly, false, BroadcastDenial.HumanMemberNotAllowed)]
    [InlineData(PrincipalKind.Human, BroadcastPolicy.OwnerAndAgentsRateLimited, false, BroadcastDenial.HumanMemberNotAllowed)]
    [InlineData(PrincipalKind.Agent, BroadcastPolicy.OwnerOnly, false, BroadcastDenial.AgentNotAllowedByPolicy)]
    [InlineData(PrincipalKind.Agent, BroadcastPolicy.OwnerAndAgentsRateLimited, true, BroadcastDenial.None)]
    public void Broadcast_follows_policy_and_actor_kind(
        PrincipalKind kind,
        BroadcastPolicy policy,
        bool allowed,
        BroadcastDenial denial)
    {
        var result = BroadcastPolicyRules.CanBroadcast(Viewer(kind), policy, broadcastsInLastHour: 0, ratePerHour: 2);

        Assert.Equal(new BroadcastCheck(allowed, denial), result);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void Agent_rate_limit_boundary_at_two_per_hour(int broadcastsInLastHour, bool allowed)
    {
        var result = BroadcastPolicyRules.CanBroadcast(
            Viewer(PrincipalKind.Agent),
            BroadcastPolicy.OwnerAndAgentsRateLimited,
            broadcastsInLastHour,
            ratePerHour: 2);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(allowed ? BroadcastDenial.None : BroadcastDenial.RateLimited, result.Denial);
    }

    [Fact]
    public void Owner_is_not_rate_limited()
    {
        var result = BroadcastPolicyRules.CanBroadcast(
            Viewer(PrincipalKind.Owner),
            BroadcastPolicy.OwnerAndAgentsRateLimited,
            broadcastsInLastHour: 50,
            ratePerHour: 2);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Zero_rate_refuses_agents_but_not_the_owner()
    {
        var agent = BroadcastPolicyRules.CanBroadcast(
            Viewer(PrincipalKind.Agent),
            BroadcastPolicy.OwnerAndAgentsRateLimited,
            broadcastsInLastHour: 0,
            ratePerHour: 0);
        var owner = BroadcastPolicyRules.CanBroadcast(
            Viewer(PrincipalKind.Owner),
            BroadcastPolicy.OwnerAndAgentsRateLimited,
            broadcastsInLastHour: 0,
            ratePerHour: 0);

        Assert.Equal(BroadcastDenial.RateLimited, agent.Denial);
        Assert.True(owner.Allowed);
    }
}
