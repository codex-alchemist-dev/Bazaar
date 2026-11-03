using Bazaar.Server.Council;
using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Tests.Council.Membership;

public class MembershipOutcomeMapperTests
{
    private static Guid VoteId => Guid.Parse("6f1c2b7e-3d4a-4e8f-9a1b-2c3d4e5f6a7b");
    private static DateTimeOffset DecidedAt => new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static MembershipMotionMetadata Motion(MotionClass motionClass = MotionClass.Ordinary) =>
        new(VoteId, MembershipMotionKind.Hire, "agent-candidate-7", motionClass, DecidedAt);

    private static TallyDecision Decided(string choice, MotionClass motionClass, IReadOnlyDictionary<string, int> counts) =>
        new(TallyDecisionKind.Decided, choice, null, counts);

    private static TallyDecision Escalated(EscalationReason reason = EscalationReason.NotUnanimous) =>
        new(TallyDecisionKind.Escalated, null, reason, new Dictionary<string, int> { ["yes"] = 2, ["no"] = 1, ["abstain"] = 0 });

    [Fact]
    public void Approved_ordinary_majority_maps_to_Approved_with_yes_choice()
    {
        var decision = Decided(ApprovalChoices.Yes, MotionClass.Ordinary, new Dictionary<string, int> { ["yes"] = 3, ["no"] = 1, ["abstain"] = 0 });

        var outcome = MembershipOutcomeMapper.Map(decision, Motion(), vaultDecisionNo: 42);

        Assert.NotNull(outcome);
        Assert.Equal(VoteId, outcome.VoteId);
        Assert.Equal("agent-candidate-7", outcome.SubjectAgentId);
        Assert.Equal(MembershipMotionKind.Hire, outcome.Kind);
        Assert.Equal(MembershipDecision.Approved, outcome.Decision);
        Assert.Equal("yes", outcome.Choice);
        Assert.Equal(DecidedAt, outcome.DecidedAt);
        Assert.Equal(42, outcome.VaultDecisionNo);
        Assert.Equal($"council.decision:{VoteId:D}", outcome.ReasonsSummaryRef);
    }

    [Fact]
    public void Ordinary_rejection_maps_to_Rejected()
    {
        var decision = Decided(ApprovalChoices.No, MotionClass.Ordinary, new Dictionary<string, int> { ["yes"] = 1, ["no"] = 3, ["abstain"] = 0 });

        var outcome = MembershipOutcomeMapper.Map(decision, Motion());

        Assert.NotNull(outcome);
        Assert.Equal(MembershipDecision.Rejected, outcome.Decision);
        Assert.Equal("no", outcome.Choice);
        Assert.Null(outcome.VaultDecisionNo);
    }

    [Fact]
    public void Unanimous_bigger_change_maps_to_Approved()
    {
        var decision = Decided(ApprovalChoices.Yes, MotionClass.BiggerChange, new Dictionary<string, int> { ["yes"] = 4, ["no"] = 0, ["abstain"] = 0 });

        var outcome = MembershipOutcomeMapper.Map(decision, Motion(MotionClass.BiggerChange));

        Assert.NotNull(outcome);
        Assert.Equal(MembershipDecision.Approved, outcome.Decision);
    }

    [Fact]
    public void Non_unanimous_decided_bigger_change_stays_pending()
    {
        var decision = Decided(ApprovalChoices.Yes, MotionClass.BiggerChange, new Dictionary<string, int> { ["yes"] = 3, ["no"] = 1, ["abstain"] = 0 });

        Assert.Null(MembershipOutcomeMapper.Map(decision, Motion(MotionClass.BiggerChange)));
    }

    [Fact]
    public void Escalated_without_owner_resolution_returns_null()
    {
        Assert.Null(MembershipOutcomeMapper.Map(Escalated(), Motion(MotionClass.BiggerChange)));
        Assert.Null(MembershipOutcomeMapper.Map(Escalated(EscalationReason.Tie), Motion(MotionClass.BiggerChange)));
    }

    [Fact]
    public void Follow_up_required_returns_null()
    {
        var decision = new TallyDecision(TallyDecisionKind.FollowUpRequired, null, null, new Dictionary<string, int> { ["yes"] = 1, ["no"] = 1 });

        Assert.Null(MembershipOutcomeMapper.Map(decision, Motion()));
    }

    [Fact]
    public void Owner_resolved_yes_maps_to_OwnerApproved()
    {
        var outcome = MembershipOutcomeMapper.Map(
            Escalated(EscalationReason.Tie),
            Motion(MotionClass.BiggerChange),
            new OwnerResolution(ApprovalChoices.Yes),
            vaultDecisionNo: 7);

        Assert.NotNull(outcome);
        Assert.Equal(MembershipDecision.OwnerApproved, outcome.Decision);
        Assert.Equal("yes", outcome.Choice);
        Assert.Equal(7, outcome.VaultDecisionNo);
    }

    [Fact]
    public void Owner_resolved_no_maps_to_OwnerRejected()
    {
        var outcome = MembershipOutcomeMapper.Map(Escalated(), Motion(MotionClass.BiggerChange), new OwnerResolution(ApprovalChoices.No));

        Assert.NotNull(outcome);
        Assert.Equal(MembershipDecision.OwnerRejected, outcome.Decision);
    }

    [Fact]
    public void Owner_resolution_for_a_decided_vote_is_refused()
    {
        var decision = Decided(ApprovalChoices.Yes, MotionClass.Ordinary, new Dictionary<string, int> { ["yes"] = 3, ["no"] = 0, ["abstain"] = 0 });

        Assert.Throws<ArgumentException>(() => MembershipOutcomeMapper.Map(decision, Motion(), new OwnerResolution(ApprovalChoices.Yes)));
    }

    [Fact]
    public void Owner_resolution_with_a_non_yes_no_choice_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            MembershipOutcomeMapper.Map(Escalated(), Motion(MotionClass.BiggerChange), new OwnerResolution("abstain")));
    }

    [Fact]
    public void Withdrawn_maps_to_Withdrawn_with_vote_id_and_no_choice()
    {
        var outcome = MembershipOutcomeMapper.MapWithdrawn(Motion(MotionClass.BiggerChange));

        Assert.Equal(VoteId, outcome.VoteId);
        Assert.Equal(MembershipDecision.Withdrawn, outcome.Decision);
        Assert.Null(outcome.Choice);
        Assert.Null(outcome.VaultDecisionNo);
        Assert.Equal(DecidedAt, outcome.DecidedAt);
    }

    [Fact]
    public void Reasons_summary_ref_never_contains_reasoning_text()
    {
        var outcome = MembershipOutcomeMapper.MapWithdrawn(Motion());

        Assert.Equal(MembershipOutcomeMapper.ReasonsSummaryRefFor(VoteId), outcome.ReasonsSummaryRef);
        Assert.DoesNotContain(' ', outcome.ReasonsSummaryRef);
    }
}
