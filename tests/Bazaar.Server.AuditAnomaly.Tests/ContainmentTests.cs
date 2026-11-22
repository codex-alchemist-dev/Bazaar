namespace Bazaar.Server.AuditAnomaly.Tests;

public class ContainmentTests
{
    private static readonly Requester Owner = new("owner", true);
    private static readonly Requester Reviewer = new("rev", false);
    private static readonly ContainmentTarget Agent = new(ContainmentScope.Agent, "agent-1");
    private static readonly ContainmentTarget Global = new(ContainmentScope.Global, null);

    [Fact]
    public void UndoWindow_IsClampedToFloor()
    {
        var s = new AnomalySettings { UndoWindow = TimeSpan.FromSeconds(1) };
        Assert.Equal(AnomalySettings.UndoWindowFloor, s.EffectiveUndoWindow);
        Assert.Equal(TimeSpan.FromHours(1), new AnomalySettings { UndoWindow = TimeSpan.FromHours(1) }.EffectiveUndoWindow);
    }

    [Fact]
    public async Task Contain_RecordsWindowAtFloor_AndAudits()
    {
        var rig = new Rig { Settings = new AnomalySettings { UndoWindow = TimeSpan.Zero } };
        var r = await rig.Containment().ContainAsync(Agent, ContainmentActions.BlockEgress | ContainmentActions.PauseSessions, "why", Reviewer, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.Done, r.Outcome);
        Assert.Equal(rig.Clock.GetUtcNow() + AnomalySettings.UndoWindowFloor, r.Record!.UndoableUntil);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == AnomalyAuditKinds.ContainmentApplied);
        Assert.Single(rig.Port.Calls);
    }

    [Fact]
    public async Task GlobalFreeze_RefusedForNonOwner_AndAudited()
    {
        var rig = new Rig();
        var r = await rig.Containment().ContainAsync(Global, ContainmentActions.FreezeWrites, "x", Reviewer, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.RefusedNotOwner, r.Outcome);
        Assert.Empty(rig.Port.Calls);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == AnomalyAuditKinds.ContainmentRefused);
    }

    [Fact]
    public async Task GlobalFreeze_AllowedForOwner()
    {
        var rig = new Rig();
        var r = await rig.Containment().ContainAsync(Global, ContainmentActions.FreezeWrites, "x", Owner, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.Done, r.Outcome);
    }

    [Theory]
    [InlineData(ContainmentActions.None)]
    [InlineData(ContainmentActions.FreezeWrites)]
    public async Task InvalidAgentRequests_Refused(ContainmentActions actions)
    {
        var rig = new Rig();
        var r = await rig.Containment().ContainAsync(Agent, actions, "x", Owner, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.RefusedInvalid, r.Outcome);
        Assert.Empty(rig.Port.Calls);
    }

    [Fact]
    public async Task Undo_WithBiometric_Restores_AndIsAudited()
    {
        var rig = new Rig();
        var svc = rig.Containment();
        var c = await svc.ContainAsync(Agent, ContainmentActions.PauseSessions, "x", Reviewer, TestContext.Current.CancellationToken);
        var u = await svc.UndoAsync(c.Record!.Id, Owner, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.Done, u.Outcome);
        Assert.Equal(ContainmentState.Undone, u.Record!.State);
        Assert.Contains(rig.Port.Calls, x => x.Op == "restore");
        var row = Assert.Single(rig.Audit.Entries, e => e.Kind == AnomalyAuditKinds.UndoApplied);
        Assert.Equal("owner", row.Actor);
    }

    [Fact]
    public async Task Undo_FailsClosed_WhenBiometricDenies_Throws_OrIsDefault()
    {
        foreach (var bio in new IBiometricCheck[] { new FakeBiometric(false), new FakeBiometric(true, throws: true), new FailClosedBiometricCheck() })
        {
            var rig = new Rig { Biometric = bio };
            var svc = rig.Containment();
            var c = await svc.ContainAsync(Agent, ContainmentActions.PauseSessions, "x", Reviewer, TestContext.Current.CancellationToken);
            var u = await svc.UndoAsync(c.Record!.Id, Owner, TestContext.Current.CancellationToken);
            Assert.Equal(ContainmentOutcome.RefusedBiometric, u.Outcome);
            Assert.DoesNotContain(rig.Port.Calls, x => x.Op == "restore");
            Assert.Contains(rig.Audit.Entries, e => e.Kind == AnomalyAuditKinds.UndoRefused);
        }
    }

    [Fact]
    public async Task Undo_ByNonOwner_Refused_WithoutAskingBiometric()
    {
        var bio = new FakeBiometric(true);
        var rig = new Rig { Biometric = bio };
        var svc = rig.Containment();
        var c = await svc.ContainAsync(Agent, ContainmentActions.PauseSessions, "x", Reviewer, TestContext.Current.CancellationToken);
        var u = await svc.UndoAsync(c.Record!.Id, Reviewer, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.RefusedBiometric, u.Outcome);
        Assert.Equal(0, bio.Calls);
    }

    [Fact]
    public async Task Undo_AfterWindow_Refused()
    {
        var rig = new Rig { Settings = new AnomalySettings { UndoWindow = TimeSpan.FromMinutes(5) } };
        var svc = rig.Containment();
        var c = await svc.ContainAsync(Agent, ContainmentActions.PauseSessions, "x", Reviewer, TestContext.Current.CancellationToken);
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(ContainmentOutcome.RefusedUndoWindowElapsed, (await svc.UndoAsync(c.Record!.Id, Owner, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task Undo_Twice_AndUnknown_Refused()
    {
        var rig = new Rig();
        var svc = rig.Containment();
        var c = await svc.ContainAsync(Agent, ContainmentActions.PauseSessions, "x", Reviewer, TestContext.Current.CancellationToken);
        await svc.UndoAsync(c.Record!.Id, Owner, TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentOutcome.RefusedAlreadyUndone, (await svc.UndoAsync(c.Record.Id, Owner, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ContainmentOutcome.RefusedUnknown, (await svc.UndoAsync(Guid.NewGuid(), Owner, TestContext.Current.CancellationToken)).Outcome);
    }
}
