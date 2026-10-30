using Bazaar.Server.Memory.Decay;
using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.Tests.Memory.Decay;

public class DecayPolicyTests
{
    private static readonly TimeSpan _halfLife = TimeSpan.FromDays(90);

    [Fact]
    public void Weight_is_one_at_zero_elapsed()
    {
        var policy = new DecayPolicy();

        Assert.Equal(1.0, policy.Weight(DecayKind.Semantic, MemoryTier.AgentVerified, TimeSpan.Zero));
    }

    [Fact]
    public void Weight_is_one_for_negative_elapsed()
    {
        var policy = new DecayPolicy();

        Assert.Equal(1.0, policy.Weight(DecayKind.Semantic, MemoryTier.AgentVerified, TimeSpan.FromDays(-3)));
    }

    [Fact]
    public void Weight_halves_after_one_half_life()
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(DecayKind.Episodic, MemoryTier.AgentVerified, _halfLife);

        Assert.Equal(0.5, weight, precision: 12);
    }

    [Fact]
    public void Weight_quarters_after_two_half_lives()
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(DecayKind.Episodic, MemoryTier.AgentVerified, _halfLife * 2);

        Assert.Equal(0.25, weight, precision: 12);
    }

    [Fact]
    public void Per_kind_half_life_overrides_the_default()
    {
        var policy = new DecayPolicy(
            defaultHalfLife: _halfLife,
            halfLifeByKind: new Dictionary<DecayKind, TimeSpan> { [DecayKind.Preference] = TimeSpan.FromDays(30) });

        Assert.Equal(0.5, policy.Weight(DecayKind.Preference, MemoryTier.AgentVerified, TimeSpan.FromDays(30)), precision: 12);
        Assert.Equal(0.5, policy.Weight(DecayKind.Episodic, MemoryTier.AgentVerified, _halfLife), precision: 12);
    }

    [Fact]
    public void Failure_floor_holds_after_a_very_long_time()
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(DecayKind.Failure, MemoryTier.AgentVerified, TimeSpan.FromDays(365 * 1000));

        Assert.Equal(DecayPolicy.FailureFloor, weight);
    }

    [Fact]
    public void Failure_floor_applies_regardless_of_tier()
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(DecayKind.Failure, MemoryTier.UntrustedDerived, TimeSpan.FromDays(365 * 1000));

        Assert.Equal(DecayPolicy.FailureFloor, weight);
    }

    [Fact]
    public void Owner_promoted_floor_holds_after_a_very_long_time()
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(DecayKind.Semantic, MemoryTier.OwnerPromoted, TimeSpan.FromDays(365 * 1000));

        Assert.Equal(DecayPolicy.OwnerPromotedFloor, weight);
    }

    [Fact]
    public void Non_floored_note_decays_toward_zero_without_reaching_it_in_practice()
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(DecayKind.Episodic, MemoryTier.AgentVerified, TimeSpan.FromDays(365 * 1000));

        Assert.True(weight >= 0.0);
        Assert.True(weight < 0.0001);
    }

    [Theory]
    [InlineData(DecayKind.Episodic, MemoryTier.Quarantined, 0)]
    [InlineData(DecayKind.Failure, MemoryTier.OwnerPromoted, 1)]
    [InlineData(DecayKind.Working, MemoryTier.UntrustedDerived, 30)]
    [InlineData(DecayKind.Hive, MemoryTier.AgentVerified, 3650)]
    [InlineData(DecayKind.Procedural, MemoryTier.OwnerPromoted, 100000)]
    public void Weight_stays_within_zero_and_one(DecayKind kind, MemoryTier tier, int days)
    {
        var policy = new DecayPolicy();

        var weight = policy.Weight(kind, tier, TimeSpan.FromDays(days));

        Assert.InRange(weight, 0.0, 1.0);
    }

    [Fact]
    public void Weight_never_increases_with_elapsed_time()
    {
        var policy = new DecayPolicy();
        var previous = double.MaxValue;

        foreach (var days in new[] { 0, 1, 10, 90, 365, 3650 })
        {
            var weight = policy.Weight(DecayKind.Semantic, MemoryTier.AgentVerified, TimeSpan.FromDays(days));
            Assert.True(weight <= previous);
            previous = weight;
        }
    }

    [Fact]
    public void Non_positive_half_life_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecayPolicy(TimeSpan.Zero));
    }
}
