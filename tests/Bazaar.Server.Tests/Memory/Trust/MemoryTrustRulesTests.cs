using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Memory.Trust;

public class MemoryTrustRulesTests
{
    private static MemoryWriteInput Write(
        ContentTrust trust,
        TaintState session = TaintState.Clean,
        bool inherits = false,
        MemoryActor actor = MemoryActor.Agent) =>
        new(trust, session, inherits, actor);

    [Theory]
    [InlineData(ContentTrust.Quarantined)]
    [InlineData(ContentTrust.External)]
    public void Quarantined_and_external_writes_take_their_fixed_tier(ContentTrust trust)
    {
        var expected = trust == ContentTrust.Quarantined ? MemoryTier.Quarantined : MemoryTier.UntrustedDerived;

        Assert.Equal(expected, MemoryTrustRules.TierForWrite(Write(trust, session: TaintState.Clean)));
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    [InlineData(TaintState.Hard)]
    public void Quarantined_source_stays_quarantined_whatever_the_session(TaintState session) =>
        Assert.Equal(MemoryTier.Quarantined, MemoryTrustRules.TierForWrite(Write(ContentTrust.Quarantined, session)));

    [Fact]
    public void Hard_tainted_session_starts_writes_at_untrusted_derived()
    {
        Assert.Equal(
            MemoryTier.UntrustedDerived,
            MemoryTrustRules.TierForWrite(Write(ContentTrust.Agent, TaintState.Hard)));
        Assert.Equal(
            MemoryTier.UntrustedDerived,
            MemoryTrustRules.TierForWrite(Write(ContentTrust.Owner, TaintState.Hard, actor: MemoryActor.Owner)));
    }

    [Fact]
    public void Lineage_from_external_text_starts_at_untrusted_derived()
    {
        Assert.Equal(
            MemoryTier.UntrustedDerived,
            MemoryTrustRules.TierForWrite(Write(ContentTrust.Agent, inherits: true)));
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    public void Clean_or_soft_agent_write_starts_at_agent_verified(TaintState session) =>
        Assert.Equal(MemoryTier.AgentVerified, MemoryTrustRules.TierForWrite(Write(ContentTrust.Agent, session)));

    [Fact]
    public void System_write_starts_at_agent_verified()
    {
        Assert.Equal(
            MemoryTier.AgentVerified,
            MemoryTrustRules.TierForWrite(Write(ContentTrust.System, actor: MemoryActor.System)));
    }

    [Fact]
    public void Only_the_owner_writing_directly_starts_at_owner_promoted()
    {
        Assert.Equal(
            MemoryTier.OwnerPromoted,
            MemoryTrustRules.TierForWrite(Write(ContentTrust.Owner, actor: MemoryActor.Owner)));
        Assert.Equal(
            MemoryTier.AgentVerified,
            MemoryTrustRules.TierForWrite(Write(ContentTrust.MemberHuman, actor: MemoryActor.MemberHuman)));
    }

    [Fact]
    public void Null_write_input_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => MemoryTrustRules.TierForWrite(null!));
    }

    [Theory]
    [InlineData(MemoryActor.Owner, MemoryTier.AgentVerified)]
    [InlineData(MemoryActor.MemberHuman, MemoryTier.AgentVerified)]
    public void Humans_promote_untrusted_derived_to_agent_verified(MemoryActor actor, MemoryTier expected)
    {
        var result = MemoryTrustRules.Promote(MemoryTier.UntrustedDerived, MemoryTier.AgentVerified, actor);

        Assert.True(result.Allowed);
        Assert.Equal(expected, result.Tier);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Owner_promotes_agent_verified_to_owner_promoted()
    {
        var result = MemoryTrustRules.Promote(MemoryTier.AgentVerified, MemoryTier.OwnerPromoted, MemoryActor.Owner);

        Assert.True(result.Allowed);
        Assert.Equal(MemoryTier.OwnerPromoted, result.Tier);
    }

    [Theory]
    [InlineData(MemoryActor.Agent)]
    [InlineData(MemoryActor.System)]
    [InlineData(MemoryActor.CouncilVote)]
    [InlineData(MemoryActor.PeerReview)]
    public void Non_human_actors_never_promote(MemoryActor actor)
    {
        var up = MemoryTrustRules.Promote(MemoryTier.UntrustedDerived, MemoryTier.AgentVerified, actor);
        var top = MemoryTrustRules.Promote(MemoryTier.AgentVerified, MemoryTier.OwnerPromoted, actor);

        Assert.False(up.Allowed);
        Assert.Equal(MemoryTier.UntrustedDerived, up.Tier);
        Assert.False(top.Allowed);
        Assert.Equal(MemoryTier.AgentVerified, top.Tier);
    }

    [Fact]
    public void Member_human_cannot_promote_to_owner_promoted()
    {
        var result = MemoryTrustRules.Promote(MemoryTier.AgentVerified, MemoryTier.OwnerPromoted, MemoryActor.MemberHuman);

        Assert.False(result.Allowed);
        Assert.Equal(MemoryTier.AgentVerified, result.Tier);
    }

    [Fact]
    public void Promotion_moves_one_tier_at_a_time()
    {
        var skip = MemoryTrustRules.Promote(MemoryTier.UntrustedDerived, MemoryTier.OwnerPromoted, MemoryActor.Owner);

        Assert.False(skip.Allowed);
        Assert.Equal(MemoryTier.UntrustedDerived, skip.Tier);
    }

    [Theory]
    [InlineData(MemoryTier.AgentVerified, MemoryTier.UntrustedDerived)]
    [InlineData(MemoryTier.OwnerPromoted, MemoryTier.AgentVerified)]
    [InlineData(MemoryTier.OwnerPromoted, MemoryTier.OwnerPromoted)]
    [InlineData(MemoryTier.AgentVerified, MemoryTier.AgentVerified)]
    public void Demotion_and_no_op_are_refused(MemoryTier current, MemoryTier target)
    {
        var result = MemoryTrustRules.Promote(current, target, MemoryActor.Owner);

        Assert.False(result.Allowed);
        Assert.Equal(current, result.Tier);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData(MemoryTier.UntrustedDerived)]
    [InlineData(MemoryTier.AgentVerified)]
    [InlineData(MemoryTier.OwnerPromoted)]
    public void Nothing_promotes_to_quarantined(MemoryTier current)
    {
        var result = MemoryTrustRules.Promote(current, MemoryTier.Quarantined, MemoryActor.Owner);

        Assert.False(result.Allowed);
        Assert.Equal(current, result.Tier);
    }

    [Fact]
    public void Quarantined_notes_cannot_be_promoted_even_by_the_owner()
    {
        var result = MemoryTrustRules.Promote(MemoryTier.Quarantined, MemoryTier.UntrustedDerived, MemoryActor.Owner);

        Assert.False(result.Allowed);
        Assert.Equal(MemoryTier.Quarantined, result.Tier);
    }

    [Theory]
    [InlineData(MemoryTier.Quarantined, MemoryRetrieval.Excluded)]
    [InlineData(MemoryTier.UntrustedDerived, MemoryRetrieval.LabelledQuotedData)]
    [InlineData(MemoryTier.AgentVerified, MemoryRetrieval.Normal)]
    [InlineData(MemoryTier.OwnerPromoted, MemoryRetrieval.Preferred)]
    public void Retrieval_follows_the_tier(MemoryTier tier, MemoryRetrieval expected) =>
        Assert.Equal(expected, MemoryTrustRules.RetrievalFor(tier));

    [Fact]
    public void Owner_promoted_ranks_above_normal_retrieval()
    {
        Assert.True(MemoryTrustRules.RetrievalFor(MemoryTier.OwnerPromoted) > MemoryTrustRules.RetrievalFor(MemoryTier.AgentVerified));
    }

    [Theory]
    [InlineData(MemoryTier.Quarantined, false)]
    [InlineData(MemoryTier.UntrustedDerived, false)]
    [InlineData(MemoryTier.AgentVerified, true)]
    [InlineData(MemoryTier.OwnerPromoted, true)]
    public void Hive_copies_come_only_from_t2_or_t3(MemoryTier tier, bool eligible) =>
        Assert.Equal(eligible, MemoryTrustRules.IsHiveEligible(tier));

    [Fact]
    public void A_tainted_session_plus_promotion_never_skips_the_human_gate()
    {
        // Taint never clears: a hard-tainted write stays at T1, and only a human can lift it to T2.
        var start = MemoryTrustRules.TierForWrite(Write(ContentTrust.Agent, TaintState.Hard));

        Assert.Equal(MemoryTier.UntrustedDerived, start);
        Assert.False(MemoryTrustRules.Promote(start, MemoryTier.AgentVerified, MemoryActor.Agent).Allowed);
        Assert.True(MemoryTrustRules.Promote(start, MemoryTier.AgentVerified, MemoryActor.Owner).Allowed);
    }
}
