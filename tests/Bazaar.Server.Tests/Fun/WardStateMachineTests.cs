using Bazaar.Server.Fun.Ward;

namespace Bazaar.Server.Tests.Fun;

public class WardStateMachineTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private const string _agent = "agent-a";
    private static readonly WardCheckIn _checkIn = new("retried the build", "lint step", "a second opinion", "the schema");

    private static WardStateMachine Machine(WardSettings? settings = null) => new(settings ?? new WardSettings());

    [Fact]
    public void Automatic_trigger_admits_when_no_stay_is_open()
    {
        var decision = Machine().Admit(_agent, WardTrigger.LoopGuard, _start, null);

        Assert.True(decision.Accepted);
        Assert.Equal(WardTrigger.LoopGuard, decision.Stay!.Trigger);
        Assert.True(decision.Stay.IsAdmitted);
    }

    [Fact]
    public void Owner_only_mode_refuses_automatic_triggers_but_admits_owner_referral()
    {
        var machine = Machine(new WardSettings { AdmissionMode = WardAdmissionMode.OwnerOnly });

        Assert.False(machine.Admit(_agent, WardTrigger.TokenHardStop, _start, null).Accepted);
        Assert.True(machine.Admit(_agent, WardTrigger.OwnerReferral, _start, null).Accepted);
    }

    [Fact]
    public void A_second_admission_is_refused_while_a_stay_is_open()
    {
        var open = Machine().Admit(_agent, WardTrigger.DeniedCallOrCanary, _start, null).Stay;

        var decision = Machine().Admit(_agent, WardTrigger.LoopGuard, _start.AddMinutes(5), open);

        Assert.False(decision.Accepted);
        Assert.Equal(open, decision.Stay);
    }

    [Fact]
    public void Admitted_agents_lose_push_merge_and_public_post_but_keep_the_inbox()
    {
        var stay = Machine().Admit(_agent, WardTrigger.LoopGuard, _start, null).Stay;

        var restrictions = WardStateMachine.RestrictionsFor(stay);

        Assert.False(restrictions.PushAllowed);
        Assert.False(restrictions.MergeAllowed);
        Assert.False(restrictions.PublicPostAllowed);
        Assert.True(restrictions.InboxKept);
        Assert.Equal(WardRestrictions.None, WardStateMachine.RestrictionsFor(null));
    }

    [Fact]
    public void Discharge_is_refused_before_the_minimum_stay()
    {
        var machine = Machine(new WardSettings { MinStayMinutes = 60 });
        var stay = machine.Admit(_agent, WardTrigger.LoopGuard, _start, null).Stay;

        var early = machine.Discharge(stay, _start.AddMinutes(59), dischargerIsOwner: true, reviewerIsClean: false);
        var onTime = machine.Discharge(stay, _start.AddMinutes(60), dischargerIsOwner: true, reviewerIsClean: false);

        Assert.False(early.Accepted);
        Assert.True(onTime.Accepted);
        Assert.Equal(_start.AddMinutes(60), onTime.Stay!.DischargedAt);
    }

    [Fact]
    public void Owner_only_discharge_refuses_a_clean_reviewer()
    {
        var machine = Machine(new WardSettings { DischargeBy = WardDischargeBy.Owner });
        var stay = machine.Admit(_agent, WardTrigger.LoopGuard, _start, null).Stay;

        var decision = machine.Discharge(stay, _start.AddHours(2), dischargerIsOwner: false, reviewerIsClean: true);

        Assert.False(decision.Accepted);
    }

    [Fact]
    public void Clean_reviewer_discharges_under_the_default_mode_and_a_tied_reviewer_does_not()
    {
        var machine = Machine();
        var stay = machine.Admit(_agent, WardTrigger.LoopGuard, _start, null).Stay;

        Assert.True(machine.Discharge(stay, _start.AddHours(2), dischargerIsOwner: false, reviewerIsClean: true).Accepted);
        Assert.False(machine.Discharge(stay, _start.AddHours(2), dischargerIsOwner: false, reviewerIsClean: false).Accepted);
    }

    [Fact]
    public void Check_in_is_recorded_on_the_active_stay_only()
    {
        var machine = Machine();
        var stay = machine.Admit(_agent, WardTrigger.SelfReferral, _start, null).Stay;

        var recorded = WardStateMachine.RecordCheckIn(_checkIn, stay);
        var discharged = machine.Discharge(stay, _start.AddHours(2), dischargerIsOwner: true, reviewerIsClean: false).Stay;

        Assert.Equal(_checkIn, recorded.Stay!.CheckIn);
        Assert.False(WardStateMachine.RecordCheckIn(_checkIn, discharged).Accepted);
    }

    [Fact]
    public void Discharged_stay_keeps_its_failure_record()
    {
        var machine = Machine();
        var stay = machine.Admit(_agent, WardTrigger.RepeatedFailure, _start, null).Stay;
        var done = machine.Discharge(stay, _start.AddHours(2), dischargerIsOwner: true, reviewerIsClean: false).Stay;

        Assert.Equal(WardTrigger.RepeatedFailure, done!.Trigger);
        Assert.Equal(_start, done.AdmittedAt);
        Assert.False(done.IsAdmitted);
    }

    [Fact]
    public void Negative_minimum_stay_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Machine(new WardSettings { MinStayMinutes = -1 }));
    }
}
