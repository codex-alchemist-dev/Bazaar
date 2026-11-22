namespace Bazaar.Server.AuditAnomaly.Tests;

public class EvaluatorTests
{
    private static AnomalySettings With(params AnomalyRule[] rules) => new() { Rules = rules };

    [Fact]
    public async Task ReadBurst_FiresAtThreshold_AndNotBelow()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("b", AnomalyRuleKind.ReadBurst, 100, TimeSpan.FromMinutes(5), AnomalySeverity.High, ObjectClass: "person")) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, type: "person", count: 60, ago: TimeSpan.FromMinutes(1)));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, type: "invoice", count: 500));
        Assert.Empty(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));

        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, type: "person", count: 40));
        var alerts = await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken);
        var a = Assert.Single(alerts);
        Assert.Equal(100, a.Observed);
        Assert.Equal(AnomalySeverity.High, a.Severity);
    }

    [Fact]
    public async Task ReadBurst_CountsPerAgent()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("b", AnomalyRuleKind.ReadBurst, 100, TimeSpan.FromMinutes(5), AnomalySeverity.High, ObjectClass: "person")) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, "a", type: "person", count: 60));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, "b", type: "person", count: 60));
        Assert.Empty(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadOutsideScope_FiresOnlyForOutOfScopeReads()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("o", AnomalyRuleKind.ReadOutsideScope, 1, TimeSpan.FromMinutes(5), AnomalySeverity.High)) };
        rig.InScope = (_, type, _) => type == "task";
        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, type: "task", id: "t1"));
        Assert.Empty(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.DataRead, type: "person", id: "p1"));
        Assert.Single(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanaryHit_FiresOnOneHit()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical)) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        var a = Assert.Single(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AnomalySeverity.Critical, a.Severity);
    }

    [Fact]
    public async Task ApprovalFlood_FiresAtThreshold()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("f", AnomalyRuleKind.ApprovalFlood, 3, TimeSpan.FromMinutes(10), AnomalySeverity.Medium)) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested));
        Assert.Empty(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested));
        Assert.Single(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Window_ExcludesOldEvents_AndSlidesWithClock()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("f", AnomalyRuleKind.ApprovalFlood, 2, TimeSpan.FromMinutes(10), AnomalySeverity.Medium)) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested, ago: TimeSpan.FromMinutes(11)));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested, ago: TimeSpan.FromMinutes(9)));
        var ev = rig.Evaluator();
        Assert.Empty(await ev.EvaluateAsync(TestContext.Current.CancellationToken));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested));
        Assert.Single(await ev.EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rule_DoesNotRefireInsideItsWindow_ThenFiresAgain()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical)) };
        var ev = rig.Evaluator();
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        Assert.Single(await ev.EvaluateAsync(TestContext.Current.CancellationToken));
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(await ev.EvaluateAsync(TestContext.Current.CancellationToken));
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        Assert.Single(await ev.EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledRule_DoesNotFire()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical, Enabled: false)) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        Assert.Empty(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Alert_IsAuditedAndSentToBothAudiences()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical)) };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == AnomalyAuditKinds.Alert && e.Payload.Contains("\"rule\":\"c\"", StringComparison.Ordinal));
        var sent = Assert.Single(rig.Sink.Sent);
        Assert.Equal(AlertAudience.OwnerDevices | AlertAudience.SecurityReviewers, sent.Audience);
    }

    [Fact]
    public async Task SinkFailure_IsAudited_AndContainmentStillApplied()
    {
        var rig = new Rig
        {
            Settings = new AnomalySettings
            {
                AutoContainmentEnabled = true,
                Rules = [new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical, Actions: ContainmentActions.BlockEgress)],
            },
        };
        rig.SinkToUse = new ThrowingSink();
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == AnomalyAuditKinds.AlertDeliveryFailed);
        Assert.Single(rig.Port.Calls);
    }

    [Fact]
    public async Task AutoContainment_IsOffByDefault_AlertOnly()
    {
        var rig = new Rig { Settings = new AnomalySettings { Rules = [new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical, Actions: ContainmentActions.BlockEgress)] } };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        var a = Assert.Single(await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
        Assert.Null(a.ContainmentId);
        Assert.Empty(rig.Port.Calls);
    }

    [Fact]
    public async Task AutoContainment_AgentScope_AndSessionScope()
    {
        var rig = new Rig
        {
            Settings = new AnomalySettings
            {
                AutoContainmentEnabled = true,
                Rules =
                [
                    new AnomalyRule("c", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical, Actions: ContainmentActions.RevokeLeases),
                    new AnomalyRule("f", AnomalyRuleKind.ApprovalFlood, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Low, Actions: ContainmentActions.PauseSessions, AutoScope: ContainmentScope.Session),
                ],
            },
        };
        rig.Events.Add(rig.Ev(AnomalyEventKinds.CanaryHit));
        rig.Events.Add(rig.Ev(AnomalyEventKinds.ApprovalRequested));
        var alerts = await rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.All(alerts, a => Assert.NotNull(a.ContainmentId));
        Assert.Contains(rig.Port.Calls, c => c.Target == new ContainmentTarget(ContainmentScope.Agent, "agent-1") && c.Actions == ContainmentActions.RevokeLeases);
        Assert.Contains(rig.Port.Calls, c => c.Target == new ContainmentTarget(ContainmentScope.Session, "s1"));
        Assert.DoesNotContain(rig.Port.Calls, c => c.Target.Scope == ContainmentScope.Global);
    }

    [Fact]
    public async Task InvalidSettings_Throw()
    {
        var rig = new Rig { Settings = With(new AnomalyRule("x", AnomalyRuleKind.ReadBurst, 0, TimeSpan.Zero, AnomalySeverity.Low)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Evaluator().EvaluateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Drafts_AreValid_AndNeverGlobal()
    {
        var s = new AnomalySettings();
        Assert.Empty(s.Validate());
        Assert.DoesNotContain(s.Rules, r => r.AutoScope == ContainmentScope.Global || r.Actions.HasFlag(ContainmentActions.FreezeWrites));
    }

    [Fact]
    public void Validate_RejectsGlobalAutoContainment()
    {
        var s = With(new AnomalyRule("g", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.High, Actions: ContainmentActions.FreezeWrites, AutoScope: ContainmentScope.Global));
        Assert.Equal(2, s.Validate().Count);
    }
}
