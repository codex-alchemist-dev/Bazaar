using System.Collections.Immutable;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Taint;

public class TaintRulesTests
{
    private static readonly Guid _source = Guid.Parse("0196a6f0-0000-7000-8000-000000000101");

    private static TaintInput Input(ContentTrust trust, TaintState sourceState = TaintState.Clean, bool inherits = false) =>
        new(_source, trust, sourceState, inherits);

    [Theory]
    [InlineData(ContentTrust.Owner, TaintState.Clean)]
    [InlineData(ContentTrust.System, TaintState.Clean)]
    [InlineData(ContentTrust.MemberHuman, TaintState.Clean)]
    [InlineData(ContentTrust.External, TaintState.Hard)]
    [InlineData(ContentTrust.Quarantined, TaintState.Clean)]
    [InlineData(ContentTrust.Agent, TaintState.Clean)]
    public void Contribution_by_tag_with_clean_agent_source(ContentTrust trust, TaintState expected) =>
        Assert.Equal(expected, TaintRules.Contribution(Input(trust), TaintPolicy.Default));

    [Theory]
    [InlineData(TaintState.Clean, TaintState.Clean)]
    [InlineData(TaintState.Soft, TaintState.Soft)]
    [InlineData(TaintState.Hard, TaintState.Soft)]
    public void Agent_text_taints_softly_and_only_from_a_tainted_source(TaintState sourceState, TaintState expected) =>
        Assert.Equal(expected, TaintRules.Contribution(Input(ContentTrust.Agent, sourceState), TaintPolicy.Default));

    [Fact]
    public void Lineage_from_external_text_makes_any_content_hard()
    {
        Assert.Equal(TaintState.Hard, TaintRules.Contribution(Input(ContentTrust.Agent, inherits: true), TaintPolicy.Default));
        Assert.Equal(TaintState.Hard, TaintRules.Contribution(Input(ContentTrust.Owner, inherits: true), TaintPolicy.Default));
    }

    [Fact]
    public void Member_humans_taint_only_when_the_setting_says_so()
    {
        Assert.Equal(TaintState.Clean, TaintRules.Contribution(Input(ContentTrust.MemberHuman), TaintPolicy.Default));
        Assert.Equal(
            TaintState.Soft,
            TaintRules.Contribution(Input(ContentTrust.MemberHuman), new TaintPolicy(MemberHumanTaints: true)));
    }

    [Theory]
    [InlineData(ContentTrust.Quarantined, false)]
    [InlineData(ContentTrust.External, true)]
    [InlineData(ContentTrust.Agent, true)]
    [InlineData(ContentTrust.Owner, true)]
    public void Only_quarantined_content_is_refused_delivery(ContentTrust trust, bool delivered) =>
        Assert.Equal(delivered, TaintRules.IsDelivered(trust));

    [Fact]
    public void A_clean_session_takes_external_content_to_hard()
    {
        var session = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.External), TaintPolicy.Default);

        Assert.Equal(TaintState.Hard, session.State);
        Assert.Equal(_source, Assert.Single(session.Sources).SourceId);
        Assert.Equal(TaintState.Hard, Assert.Single(session.Sources).Raised);
    }

    [Fact]
    public void Owner_content_leaves_a_clean_session_clean_and_records_nothing()
    {
        var session = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.Owner), TaintPolicy.Default);

        Assert.Equal(SessionTaint.Clean, session);
        Assert.Empty(session.Sources);
    }

    [Fact]
    public void Taint_never_goes_back_down()
    {
        var hard = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.External), TaintPolicy.Default);

        var after = TaintRules.Apply(hard, Input(ContentTrust.Owner), TaintPolicy.Default);
        var soft = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.Agent, TaintState.Soft), TaintPolicy.Default);
        var afterSoft = TaintRules.Apply(soft, Input(ContentTrust.Owner), TaintPolicy.Default);

        Assert.Equal(TaintState.Hard, after.State);
        Assert.Equal(TaintState.Soft, afterSoft.State);
    }

    [Fact]
    public void Soft_then_hard_escalates_and_keeps_both_sources()
    {
        var soft = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.Agent, TaintState.Soft), TaintPolicy.Default);
        var hard = TaintRules.Apply(soft, new TaintInput(Guid.NewGuid(), ContentTrust.External), TaintPolicy.Default);

        Assert.Equal(TaintState.Hard, hard.State);
        Assert.Equal(2, hard.Sources.Length);
        Assert.Equal(TaintState.Soft, hard.Sources[0].Raised);
        Assert.Equal(TaintState.Hard, hard.Sources[1].Raised);
    }

    [Fact]
    public void Quarantined_content_leaves_the_session_unchanged()
    {
        var hard = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.External), TaintPolicy.Default);

        var after = TaintRules.Apply(hard, new TaintInput(Guid.NewGuid(), ContentTrust.Quarantined), TaintPolicy.Default);

        Assert.Equal(hard, after);
    }

    [Fact]
    public void Same_source_is_recorded_once()
    {
        var once = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.External), TaintPolicy.Default);
        var twice = TaintRules.Apply(once, Input(ContentTrust.External), TaintPolicy.Default);

        Assert.Equal(once, twice);
        Assert.Single(twice.Sources);
    }

    [Fact]
    public void Lineage_source_is_recorded_as_external()
    {
        var session = TaintRules.Apply(SessionTaint.Clean, Input(ContentTrust.Agent, inherits: true), TaintPolicy.Default);

        Assert.Equal(ContentTrust.External, Assert.Single(session.Sources).Trust);
    }

    [Theory]
    [InlineData(TaintState.Clean, false)]
    [InlineData(TaintState.Soft, false)]
    [InlineData(TaintState.Hard, true)]
    public void Only_hard_taint_blocks_tier_2(TaintState state, bool blocked) =>
        Assert.Equal(blocked, TaintRules.BlocksTier2(state));

    [Fact]
    public void Clean_session_has_no_sources()
    {
        Assert.Equal(TaintState.Clean, SessionTaint.Clean.State);
        Assert.Equal(ImmutableArray<TaintSource>.Empty, SessionTaint.Clean.Sources);
    }
}
