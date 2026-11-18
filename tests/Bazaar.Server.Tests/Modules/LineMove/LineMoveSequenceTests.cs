using Bazaar.Server.Modules.LineMove;

namespace Bazaar.Server.Tests.Modules.LineMove;

/// <summary>The step order as data: a step runs only after its predecessor succeeded.</summary>
public sealed class LineMoveSequenceTests
{
    [Fact]
    public void The_forward_order_is_preflight_snapshot_dry_run_switch_verify_complete()
    {
        Assert.Equal(
            [LineMoveStep.Preflight, LineMoveStep.Snapshot, LineMoveStep.DryRun, LineMoveStep.Switch, LineMoveStep.Verify, LineMoveStep.Complete],
            LineMoveSequence.Forward);
    }

    [Fact]
    public void Preflight_is_the_only_step_with_no_predecessor()
    {
        Assert.Null(LineMoveSequence.PredecessorOf(LineMoveStep.Preflight));
        Assert.Equal(LineMoveStep.Preflight, LineMoveSequence.PredecessorOf(LineMoveStep.Snapshot));
        Assert.Equal(LineMoveStep.Verify, LineMoveSequence.PredecessorOf(LineMoveStep.Complete));
    }

    [Fact]
    public void Rollback_follows_the_switch()
    {
        Assert.Equal(LineMoveStep.Switch, LineMoveSequence.PredecessorOf(LineMoveStep.RolledBack));
    }

    [Fact]
    public void A_step_cannot_run_before_its_predecessor_succeeded()
    {
        Assert.Throws<InvalidOperationException>(() => LineMoveSequence.EnsureCanRun(LineMoveStep.Snapshot, lastSucceeded: null));
        Assert.Throws<InvalidOperationException>(() => LineMoveSequence.EnsureCanRun(LineMoveStep.Switch, LineMoveStep.Snapshot));
        Assert.Throws<InvalidOperationException>(() => LineMoveSequence.EnsureCanRun(LineMoveStep.Complete, LineMoveStep.Switch));
    }

    [Fact]
    public void A_step_may_run_directly_after_its_predecessor()
    {
        LineMoveSequence.EnsureCanRun(LineMoveStep.Preflight, lastSucceeded: null);
        LineMoveSequence.EnsureCanRun(LineMoveStep.Snapshot, LineMoveStep.Preflight);
        LineMoveSequence.EnsureCanRun(LineMoveStep.Verify, LineMoveStep.Switch);
        LineMoveSequence.EnsureCanRun(LineMoveStep.RolledBack, LineMoveStep.Switch);
    }

    [Fact]
    public void Rollback_cannot_run_after_a_step_other_than_the_switch()
    {
        Assert.False(LineMoveSequence.CanRun(LineMoveStep.RolledBack, LineMoveStep.Verify));
        Assert.False(LineMoveSequence.CanRun(LineMoveStep.RolledBack, LineMoveStep.DryRun));
    }
}
