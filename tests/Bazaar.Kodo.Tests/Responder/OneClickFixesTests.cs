using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Responder;

public sealed class OneClickFixesTests
{
    [Fact]
    public void EveryFixIsAValidSignableRunbookWithOneActionAndAVerifyStep()
    {
        Assert.Equal(8, OneClickFixes.All.Count);
        Assert.Equal(OneClickFixes.All.Count, OneClickFixes.All.Select(f => f.Id).Distinct().Count());

        foreach (OneClickFix fix in OneClickFixes.All)
        {
            Assert.Null(fix.Definition.Problem());
            Assert.Equal(fix.Id, fix.Definition.Id);
            Assert.Contains(fix.RequiredControl, KodoControls.All);
            Assert.Single(fix.Definition.Steps, s => s.Kind == RunbookStepKind.Action);
            Assert.Contains(fix.Definition.Steps, s => s.Kind == RunbookStepKind.Verify);
        }
    }

    [Fact]
    public void EveryFixIsAcceptedByACatalogueOnceSigned()
    {
        RunbookCatalogue catalogue = ResponderTestKit.Catalogue([.. OneClickFixes.All.Select(f => f.Definition)]);

        Assert.Equal(8, catalogue.Count);
    }

    [Fact]
    public void RestartSlotSwitchAndIsolateFollowTheStepUpRuleOf14Point5()
    {
        Assert.Equal(RunbookApprovalTier.ApprovalStepUp, OneClickFixes.For("host.ready").Single().ApprovalTier);
        Assert.Equal(KodoControls.ControlRestart, OneClickFixes.For("host.ready").Single().RequiredControl);
        Assert.Equal(KodoControls.ControlRollback, OneClickFixes.For("kodo.self.signature_verify").Single().RequiredControl);
        Assert.Equal(KodoControls.ControlIsolate, OneClickFixes.For("module.health_contract").Single().RequiredControl);
        Assert.Equal(RunbookApprovalTier.ApprovalStepUp, OneClickFixes.For("replication.status").Single().ApprovalTier);
    }

    [Fact]
    public void OnlyLowRiskFixesAcceptNoApprovalAndNoneIsUnattendedByDefault()
    {
        IEnumerable<OneClickFix> noApproval = OneClickFixes.All.Where(f => f.ApprovalTier == RunbookApprovalTier.None);

        Assert.All(noApproval, f => Assert.Equal(RunbookRisk.Low, f.Definition.Risk));
        Assert.Empty(new RunbookOptions().AutoRunbookIds);
    }

    [Fact]
    public void ReversibleFixesCarryARollbackAndUnknownTriggersSuggestNothing()
    {
        Assert.NotEmpty(OneClickFixes.For("kodo.self.signature_verify").Single().Definition.Rollback);
        Assert.NotEmpty(OneClickFixes.For("model.lane_quota").Single().Definition.Rollback);
        Assert.Empty(OneClickFixes.For("nothing.here"));
    }

    [Fact]
    public async Task FixRunsEndToEndOnlyAfterAnExplicitApproval()
    {
        OneClickFix fix = OneClickFixes.For("host.ready").Single();
        Harness h = new(fix.Definition);
        RunbookStartRequest request = new(
            "fix-run", fix.Id, 1, null, "owner", new HashSet<string> { fix.RequiredControl },
            new Dictionary<string, string>(), false, Bazaar.Kodo.Evaluator.KodoState.Failing);

        RunbookRunState waiting = (await h.Executor().StartAsync(request, CancellationToken.None)).State!;
        Assert.Equal(RunbookPhase.AwaitingApproval, waiting.Phase);
        Assert.True(h.Gate.Requests.Single().RequireStepUp);
        Assert.DoesNotContain(h.Runner.Calls, c => c.Operation == "sentinel.restart_host");

        h.ApproveAll();
        RunbookRunState done = (await h.Executor().ResumeAsync("fix-run", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.Succeeded, done.Phase);
        Assert.Contains(h.Runner.Calls, c => c.Operation == "sentinel.restart_host");
    }
}
