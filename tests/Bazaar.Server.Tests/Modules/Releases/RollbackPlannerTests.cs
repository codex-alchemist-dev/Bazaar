using Bazaar.Server.Modules.Releases;

namespace Bazaar.Server.Tests.Modules.Releases;

/// <summary>Rollback planning for a halted release (doc 49 section 6). The planner is pure, so no clock or I/O is needed.</summary>
public sealed class RollbackPlannerTests
{
    private const string _halted = "0.2.0";
    private const string _previous = "0.1.0";

    [Fact]
    public void Canary_and_early_installs_on_the_halted_version_are_rolled_back_to_the_previous_version()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("install-a", ReleaseRing.Canary, _halted),
            new InstallRingMembership("install-b", ReleaseRing.Early, _halted),
        ]);

        Assert.Equal(RollbackPlanOutcome.Planned, plan.Outcome);
        Assert.Null(plan.Reason);
        Assert.Collection(plan.Actions,
            action => Assert.Equal(new RollbackAction("install-a", ReleaseRing.Canary, RollbackActionKind.RollBack, _halted, _previous), action),
            action => Assert.Equal(new RollbackAction("install-b", ReleaseRing.Early, RollbackActionKind.RollBack, _halted, _previous), action));
    }

    [Fact]
    public void Broad_installs_on_the_halted_version_await_the_owner_and_are_not_rolled_back()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("install-broad", ReleaseRing.Broad, _halted),
        ]);

        Assert.Equal(RollbackPlanOutcome.Planned, plan.Outcome);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(new RollbackAction("install-broad", ReleaseRing.Broad, RollbackActionKind.AwaitingOwnerDecision, _halted, _previous), action);
    }

    [Fact]
    public void Mixed_rings_produce_rollback_for_canary_and_early_and_owner_decision_for_broad()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("b", ReleaseRing.Broad, _halted),
            new InstallRingMembership("e", ReleaseRing.Early, _halted),
            new InstallRingMembership("c", ReleaseRing.Canary, _halted),
        ]);

        Assert.Collection(plan.Actions,
            action => Assert.Equal((ReleaseRing.Canary, RollbackActionKind.RollBack), (action.Ring, action.Kind)),
            action => Assert.Equal((ReleaseRing.Early, RollbackActionKind.RollBack), (action.Ring, action.Kind)),
            action => Assert.Equal((ReleaseRing.Broad, RollbackActionKind.AwaitingOwnerDecision), (action.Ring, action.Kind)));
    }

    [Fact]
    public void Null_previous_version_is_refused_with_no_actions()
    {
        var plan = RollbackPlanner.Plan(_halted, null,
        [
            new InstallRingMembership("install-a", ReleaseRing.Canary, _halted),
        ]);

        Assert.Equal(RollbackPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(RollbackRefusalReason.NoPreviousVersion, plan.Reason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Previous_version_equal_to_halted_version_is_refused_with_no_actions()
    {
        var plan = RollbackPlanner.Plan(_halted, _halted,
        [
            new InstallRingMembership("install-a", ReleaseRing.Canary, _halted),
        ]);

        Assert.Equal(RollbackPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(RollbackRefusalReason.NoPreviousVersion, plan.Reason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Installs_already_on_the_previous_version_produce_no_action()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("already-back", ReleaseRing.Canary, _previous),
            new InstallRingMembership("still-halted", ReleaseRing.Early, _halted),
        ]);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("still-halted", action.InstallId);
    }

    [Fact]
    public void Installs_on_an_unrelated_version_produce_no_action()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("on-next", ReleaseRing.Canary, "0.3.0"),
        ]);

        Assert.Equal(RollbackPlanOutcome.Planned, plan.Outcome);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Actions_are_ordered_by_ring_then_install_id_ordinal_whatever_the_input_order()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("zeta", ReleaseRing.Early, _halted),
            new InstallRingMembership("beta", ReleaseRing.Canary, _halted),
            new InstallRingMembership("Alpha", ReleaseRing.Canary, _halted),
            new InstallRingMembership("gamma", ReleaseRing.Broad, _halted),
            new InstallRingMembership("alpha", ReleaseRing.Early, _halted),
        ]);

        Assert.Equal(
            ["Alpha", "beta", "alpha", "zeta", "gamma"],
            plan.Actions.Select(a => a.InstallId).ToArray());
    }

    [Fact]
    public void Same_input_in_a_different_order_gives_the_same_plan()
    {
        var first = new InstallRingMembership("one", ReleaseRing.Canary, _halted);
        var second = new InstallRingMembership("two", ReleaseRing.Early, _halted);

        var forward = RollbackPlanner.Plan(_halted, _previous, [first, second]);
        var reversed = RollbackPlanner.Plan(_halted, _previous, [second, first]);

        Assert.Equal(forward.Actions, reversed.Actions);
    }

    [Fact]
    public void Empty_input_is_planned_with_no_actions()
    {
        var plan = RollbackPlanner.Plan(_halted, _previous, []);

        Assert.Equal(RollbackPlanOutcome.Planned, plan.Outcome);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Duplicate_install_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RollbackPlanner.Plan(_halted, _previous,
        [
            new InstallRingMembership("same", ReleaseRing.Canary, _halted),
            new InstallRingMembership("same", ReleaseRing.Early, _halted),
        ]));
    }

    [Fact]
    public void Blank_halted_version_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RollbackPlanner.Plan(" ", _previous, []));
    }
}
