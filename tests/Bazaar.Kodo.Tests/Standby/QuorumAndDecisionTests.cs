using Bazaar.Kodo.Standby;

namespace Bazaar.Kodo.Tests.Standby;

public sealed class QuorumAndDecisionTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _skew = TimeSpan.FromSeconds(3);

    private static WitnessQuorumPolicy Policy() => new(["w1", "w2", "w3"]);

    private static WitnessReport R(string id, WitnessAnswer answer) => new(id, answer, null);

    [Fact]
    public void DefaultIsStrictMajority()
    {
        Assert.Equal(2, Policy().Required);
        Assert.Equal(2, new WitnessQuorumPolicy(["a", "b"]).Required);
        Assert.Equal(3, new WitnessQuorumPolicy(["a", "b", "c", "d", "e"]).Required);
        Assert.Equal(3, new WitnessQuorumPolicy(["a", "b", "c", "d"]).Required);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(4)]
    public void PolicyRefusesBelowFloorOrAboveAll(int required) =>
        Assert.Throws<ArgumentException>(() => new WitnessQuorumPolicy(["a", "b", "c"], required));

    [Fact]
    public void PolicyMayRaiseTheCount() => Assert.Equal(3, new WitnessQuorumPolicy(["a", "b", "c"], 3).Required);

    [Fact]
    public void PolicyRefusesBadSets()
    {
        Assert.Throws<ArgumentException>(() => new WitnessQuorumPolicy(["a"]));
        Assert.Throws<ArgumentException>(() => new WitnessQuorumPolicy(["a", "a"]));
        Assert.Throws<ArgumentException>(() => new WitnessQuorumPolicy(["a", " "]));
    }

    [Fact]
    public void EvaluatorCountsEachWitnessOnceAndIgnoresStrangers()
    {
        QuorumResult result = Policy().EvaluateUnreachable(
        [
            R("w1", WitnessAnswer.PrimaryUnreachable), R("w1", WitnessAnswer.PrimaryUnreachable),
            R("zz", WitnessAnswer.PrimaryUnreachable), R("zz2", WitnessAnswer.PrimaryUnreachable),
        ]);
        Assert.Equal(1, result.Agreeing);
        Assert.False(result.Met);
    }

    [Fact]
    public void ConflictingDuplicateCountsAsNoAnswer()
    {
        QuorumResult result = Policy().EvaluateUnreachable(
            [R("w1", WitnessAnswer.PrimaryUnreachable), R("w1", WitnessAnswer.PrimaryReachable), R("w2", WitnessAnswer.PrimaryUnreachable)]);
        Assert.Equal(1, result.Agreeing);
        Assert.Equal(0, result.Reachable);
    }

    [Fact]
    public void GrantTallyDedupes()
    {
        Assert.False(Policy().EvaluateGrants(["w1", "w1", "x"]).Met);
        Assert.True(Policy().EvaluateGrants(["w1", "w3"]).Met);
    }

    private static FailoverInput Input(StandbyRole role = StandbyRole.Standby, LeaseRecord? lease = null, int unreachable = 2, int reachable = 0, double nowSeconds = 20)
    {
        lease ??= LeaseRecord.Create("p", new FencingToken(4), _t0, TimeSpan.FromSeconds(10));
        return new FailoverInput(role, lease, 4, _t0.AddSeconds(nowSeconds), _skew, new QuorumResult(unreachable, 2, reachable, unreachable >= 2));
    }

    [Fact]
    public void PromotesOnlyWhenEverythingHolds()
    {
        FailoverDecision decision = FailoverDecider.Decide(Input());
        Assert.Equal(FailoverAction.Promote, decision.Action);
        Assert.Equal(5, decision.NewEpoch.Epoch);
    }

    [Fact]
    public void NewEpochIsAboveHighestSeen()
    {
        FailoverInput input = Input() with { HighestEpochSeen = 9 };
        Assert.Equal(10, FailoverDecider.Decide(input).NewEpoch.Epoch);
    }

    [Theory]
    [InlineData(StandbyRole.Primary, FailoverReasons.NotStandby)]
    [InlineData(StandbyRole.Fenced, FailoverReasons.NotStandby)]
    public void OnlyStandbyPromotes(StandbyRole role, string reason) =>
        Assert.Equal(reason, FailoverDecider.Decide(Input(role)).Reason);

    [Fact]
    public void NoPromotionWithoutLease() =>
        Assert.Equal(FailoverReasons.NoLeaseKnown, FailoverDecider.Decide(Input() with { KnownLease = null }).Reason);

    [Theory]
    [InlineData(9.0)]
    [InlineData(10.0)]
    [InlineData(12.9)]
    public void NoPromotionBeforeExpiryPlusMargin(double nowSeconds) =>
        Assert.Equal(FailoverReasons.LeaseNotExpired, FailoverDecider.Decide(Input(nowSeconds: nowSeconds)).Reason);

    [Fact]
    public void NoPromotionWhenAnyWitnessSeesPrimary() =>
        Assert.Equal(FailoverReasons.PrimaryCanRenew, FailoverDecider.Decide(Input(unreachable: 2, reachable: 1)).Reason);

    [Fact]
    public void NoPromotionWithoutQuorum() =>
        Assert.Equal(FailoverReasons.NoQuorum, FailoverDecider.Decide(Input(unreachable: 1)).Reason);
}
