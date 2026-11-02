using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Retirement;

namespace Bazaar.Server.Tests.Souls.Retirement;

/// <summary>Retirement ordering, idempotency, partial failure and disk rules (docs/design/07-soul-system.md, WP6).</summary>
public sealed class RetirementCoordinatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static SoulRetirementSubject Subject(Guid? agentId = null) => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), agentId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static VmChildDisk Child(DateTimeOffset retainUntil) => new(Guid.NewGuid(), "child-disk-1", "parent-image-1", retainUntil);

    private static VmParentDisk Parent(int otherChildren = 0, bool retired = true) => new("parent-image-1", "parent-disk-1", otherChildren, retired);

    private static RetirementRequest Full(SoulRetirementSubject? subject = null) =>
        new(subject ?? Subject(), Child(_now.AddDays(-1)), Parent());

    private static RetirementStepResult Step(RetirementReport report, RetirementStepKind kind) =>
        report.Steps.Single(s => s.Step == kind);

    [Fact]
    public async Task Runs_steps_in_order_and_completes()
    {
        var rig = new RetirementRig(_now);

        var report = await rig.Coordinator.RetireAsync(Full(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [RetirementStepKind.PassportRevocation, RetirementStepKind.TokenRevocation, RetirementStepKind.VmStop, RetirementStepKind.VmChildDiskDelete, RetirementStepKind.VmParentDiskDelete],
            report.Steps.Select(s => s.Step));
        Assert.Equal(["passport", "token", "vm-stop", "child-delete", "parent-delete"], rig.Log.Entries);
        Assert.All(report.Steps, s => Assert.Equal(RetirementStepStatus.Done, s.Status));
        Assert.True(report.IsComplete);
        Assert.False(report.HasFailures);
        Assert.Equal(Subject().SoulId, report.SoulId);
        Assert.Equal(Subject().AgentId, report.AgentId);
    }

    [Fact]
    public async Task Rerun_after_success_is_a_no_op()
    {
        var rig = new RetirementRig(_now);
        var request = Full();
        await rig.Coordinator.RetireAsync(request, TestContext.Current.CancellationToken);
        var callsAfterFirst = rig.Log.Entries.Count;

        var second = await rig.Coordinator.RetireAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(callsAfterFirst, rig.Log.Entries.Count);
        Assert.All(second.Steps, s => Assert.Equal(RetirementStepStatus.AlreadyDone, s.Status));
        Assert.True(second.IsComplete);
        Assert.Equal(1, rig.Passports.Op.Calls);
        Assert.Equal(1, rig.Tokens.Op.Calls);
        Assert.Equal(1, rig.Disks.ChildDelete.Calls);
    }

    [Fact]
    public async Task Passport_failure_does_not_skip_later_steps_and_a_rerun_finishes_only_what_failed()
    {
        var rig = new RetirementRig(_now);
        rig.Passports.Op.Fail = new InvalidOperationException("passport store unavailable");

        var first = await rig.Coordinator.RetireAsync(Full(), TestContext.Current.CancellationToken);

        var passport = Step(first, RetirementStepKind.PassportRevocation);
        Assert.Equal(RetirementStepStatus.Failed, passport.Status);
        Assert.Equal("passport store unavailable", passport.Reason);
        Assert.Equal(RetirementStepStatus.Done, Step(first, RetirementStepKind.TokenRevocation).Status);
        Assert.Equal(RetirementStepStatus.Done, Step(first, RetirementStepKind.VmStop).Status);
        Assert.Equal(RetirementStepStatus.Done, Step(first, RetirementStepKind.VmChildDiskDelete).Status);
        Assert.True(first.HasFailures);
        Assert.False(first.IsComplete);

        rig.Passports.Op.Fail = null;
        var second = await rig.Coordinator.RetireAsync(Full(), TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Done, Step(second, RetirementStepKind.PassportRevocation).Status);
        Assert.Equal(RetirementStepStatus.AlreadyDone, Step(second, RetirementStepKind.TokenRevocation).Status);
        Assert.Equal(RetirementStepStatus.AlreadyDone, Step(second, RetirementStepKind.VmChildDiskDelete).Status);
        Assert.True(second.IsComplete);
        Assert.Equal(1, rig.Tokens.Op.Calls);
        Assert.Equal(2, rig.Passports.Op.Calls);
    }

    [Fact]
    public async Task Token_failure_still_runs_the_vm_steps()
    {
        var rig = new RetirementRig(_now);
        rig.Tokens.Op.Fail = new InvalidOperationException("identity service timeout");

        var report = await rig.Coordinator.RetireAsync(Full(), TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Failed, Step(report, RetirementStepKind.TokenRevocation).Status);
        Assert.Equal(RetirementStepStatus.Done, Step(report, RetirementStepKind.VmParentDiskDelete).Status);
        Assert.Contains("child-delete", rig.Log.Entries);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public async Task Stop_failure_is_recorded_and_the_child_delete_is_still_attempted()
    {
        var rig = new RetirementRig(_now);
        rig.Disks.Stop.Fail = new InvalidOperationException("hypervisor refused stop");

        var report = await rig.Coordinator.RetireAsync(Full(), TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Failed, Step(report, RetirementStepKind.VmStop).Status);
        Assert.Equal("hypervisor refused stop", Step(report, RetirementStepKind.VmStop).Reason);
        Assert.Equal(1, rig.Disks.ChildDelete.Calls);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public async Task Port_status_other_than_a_success_is_reported_as_failed()
    {
        var rig = new RetirementRig(_now);
        rig.Tokens.Op.Override = RetirementStepStatus.Refused;

        var report = await rig.Coordinator.RetireAsync(new RetirementRequest(Subject()), TestContext.Current.CancellationToken);

        var token = Step(report, RetirementStepKind.TokenRevocation);
        Assert.Equal(RetirementStepStatus.Failed, token.Status);
        Assert.Equal("Port reported Refused, not a success.", token.Reason);
    }

    [Fact]
    public async Task Parent_with_other_live_children_is_refused_and_never_deleted()
    {
        var rig = new RetirementRig(_now);
        var request = new RetirementRequest(Subject(), Child(_now.AddDays(-1)), Parent(otherChildren: 2));

        var report = await rig.Coordinator.RetireAsync(request, TestContext.Current.CancellationToken);

        var parent = Step(report, RetirementStepKind.VmParentDiskDelete);
        Assert.Equal(RetirementStepStatus.Refused, parent.Status);
        Assert.Contains("2 live child disk", parent.Reason);
        Assert.Equal(0, rig.Disks.ParentDelete.Calls);
        Assert.False(report.IsComplete);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public async Task Parent_whose_image_is_not_retired_is_refused_and_never_deleted()
    {
        var rig = new RetirementRig(_now);
        var request = new RetirementRequest(Subject(), Child(_now.AddDays(-1)), Parent(retired: false));

        var report = await rig.Coordinator.RetireAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Refused, Step(report, RetirementStepKind.VmParentDiskDelete).Status);
        Assert.Equal(0, rig.Disks.ParentDelete.Calls);
    }

    [Fact]
    public async Task Retired_parent_with_no_other_children_is_deleted()
    {
        var rig = new RetirementRig(_now);

        var report = await rig.Coordinator.RetireAsync(Full(), TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Done, Step(report, RetirementStepKind.VmParentDiskDelete).Status);
        Assert.Equal(1, rig.Disks.ParentDelete.Calls);
    }

    [Fact]
    public async Task Child_inside_retention_is_deferred_then_deleted_once_retention_has_passed()
    {
        var rig = new RetirementRig(_now);
        var retainUntil = _now.AddDays(30);
        var request = new RetirementRequest(Subject(), Child(retainUntil));

        var first = await rig.Coordinator.RetireAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Done, Step(first, RetirementStepKind.VmStop).Status);
        var deferred = Step(first, RetirementStepKind.VmChildDiskDelete);
        Assert.Equal(RetirementStepStatus.Deferred, deferred.Status);
        Assert.Contains(retainUntil.ToString("O"), deferred.Reason);
        Assert.Equal(0, rig.Disks.ChildDelete.Calls);
        Assert.False(first.IsComplete);
        Assert.False(first.HasFailures);

        rig.Clock.Now = retainUntil.AddSeconds(1);
        var second = await rig.Coordinator.RetireAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.AlreadyDone, Step(second, RetirementStepKind.VmStop).Status);
        Assert.Equal(RetirementStepStatus.Done, Step(second, RetirementStepKind.VmChildDiskDelete).Status);
        Assert.True(second.IsComplete);
        Assert.Equal(1, rig.Disks.Stop.Calls);
        Assert.Equal(1, rig.Disks.ChildDelete.Calls);
    }

    [Fact]
    public async Task Request_without_vm_disks_runs_only_the_revocation_steps()
    {
        var rig = new RetirementRig(_now);

        var report = await rig.Coordinator.RetireAsync(new RetirementRequest(Subject()), TestContext.Current.CancellationToken);

        Assert.Equal([RetirementStepKind.PassportRevocation, RetirementStepKind.TokenRevocation], report.Steps.Select(s => s.Step));
        Assert.True(report.IsComplete);
        Assert.Equal(0, rig.Disks.Stop.Calls + rig.Disks.ChildDelete.Calls + rig.Disks.ParentDelete.Calls);
    }

    [Fact]
    public async Task Ledger_is_scoped_per_agent_so_a_second_agent_of_the_same_soul_is_still_retired()
    {
        var rig = new RetirementRig(_now);
        await rig.Coordinator.RetireAsync(new RetirementRequest(Subject(Guid.Parse("33333333-3333-3333-3333-333333333333"))), TestContext.Current.CancellationToken);

        var other = await rig.Coordinator.RetireAsync(new RetirementRequest(Subject(Guid.Parse("44444444-4444-4444-4444-444444444444"))), TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Done, Step(other, RetirementStepKind.PassportRevocation).Status);
        Assert.Equal(2, rig.Passports.Op.Calls);
    }

    [Fact]
    public async Task Ledger_records_each_step_outcome()
    {
        var rig = new RetirementRig(_now);
        var subject = Subject();

        await rig.Coordinator.RetireAsync(new RetirementRequest(subject, Child(_now.AddDays(30))), TestContext.Current.CancellationToken);
        var recorded = await rig.Ledger.ReadAsync(subject, TestContext.Current.CancellationToken);

        Assert.Equal(RetirementStepStatus.Deferred, recorded.Single(r => r.Step == RetirementStepKind.VmChildDiskDelete).Status);
        Assert.Equal(RetirementStepStatus.Done, recorded.Single(r => r.Step == RetirementStepKind.PassportRevocation).Status);
    }
}
