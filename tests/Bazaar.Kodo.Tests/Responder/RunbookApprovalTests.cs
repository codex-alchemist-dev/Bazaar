using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Responder;

public sealed class RunbookApprovalTests
{
    [Fact]
    public async Task PendingApprovalHoldsTheRunBeforeAnyActionAndResumeAsksAgain()
    {
        Harness h = new();
        RunbookExecutor executor = h.Executor();

        RunbookRunState state = (await executor.StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
        Assert.Equal(["pre"], h.Runner.Calls.Select(c => c.StepId));
        Assert.Contains("approval_requested:do_a", h.Journal.Kinds);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
        Assert.Equal(2, h.Gate.Requests.Count);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");

        h.ApproveAll();
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.Succeeded, state.Phase);
    }

    [Fact]
    public async Task ApprovalWaitBoundaryStopsForAHumanAndNeverApproves()
    {
        Harness h = new(options: new RunbookOptions { ApprovalWait = TimeSpan.FromHours(1) });
        RunbookExecutor executor = h.Executor();
        await executor.StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        h.Clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        RunbookRunState state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("approval_timeout", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task GateTimedOutDecisionIsNeverApproval()
    {
        Harness h = new();
        h.Gate.Decide = _ => ApprovalDecision.TimedOut;

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("approval_timeout", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task DeniedBeforeAnyActionEndsDeniedWithNothingChanged()
    {
        Harness h = new();
        h.Gate.Decide = _ => ApprovalDecision.Denied;

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.Denied, state.Phase);
        Assert.Equal("approval_denied", state.Cause);
        Assert.Equal(["pre"], h.Runner.Calls.Select(c => c.StepId));
        Assert.Contains("approval_denied:do_a", h.Journal.Kinds);
    }

    [Fact]
    public async Task DeniedAfterAnApprovedActionRollsBackThenEndsDenied()
    {
        Harness h = new();
        h.Gate.Decide = request => request.StepId == "do_a" ? ApprovalDecision.Approved : ApprovalDecision.Denied;
        RunbookExecutor executor = h.Executor();

        RunbookRunState state = (await executor.StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        // Rolling back also needs an approval in a gated run, which is denied here, so the run stops for a human.
        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("rollback_not_approved", state.Cause);
        Assert.Contains(h.Runner.Calls, c => c.StepId == "do_a");
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_b");
    }

    [Fact]
    public async Task DeniedAfterAnApprovedActionRollsBackWhenTheRollbackIsApproved()
    {
        Harness h = new();
        h.Gate.Decide = request => request.StepId is "do_b" ? ApprovalDecision.Denied : ApprovalDecision.Approved;

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.Denied, state.Phase);
        Assert.Equal("undo_a", h.Runner.Calls[^1].StepId);
    }

    [Fact]
    public async Task ApprovalForADifferentRequestIsRefused()
    {
        Harness h = new();
        h.Gate.Decide = _ => ApprovalDecision.Approved;
        h.Gate.DigestOverride = new string('0', 64);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("approval_mismatch", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task HighRiskNeedsStepUpAndAnApprovalWithoutItIsRefused()
    {
        RunbookDefinition high = ResponderTestKit.Demo(RunbookRisk.High, RunbookApprovalTier.ApprovalStepUp);
        Harness h = new(high);
        h.Gate.Decide = _ => ApprovalDecision.Approved;
        h.Gate.StepUp = false;

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(definition: high), CancellationToken.None)).State!;

        Assert.True(h.Gate.Requests[0].RequireStepUp);
        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("step_up_missing", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task GateErrorStopsTheRun()
    {
        Harness h = new();
        h.Gate.Throws = true;

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("approval_gate_error", state.Cause);
    }

    [Fact]
    public async Task StepFlaggedForApprovalIsGatedInAnOtherwiseUnattendedRun()
    {
        RunbookDefinition d = ResponderTestKit.Demo(RunbookRisk.Low, RunbookApprovalTier.None) with
        {
            Steps =
            [
                ResponderTestKit.Step("do_a", RunbookStepKind.Action, "demo.do_a"),
                ResponderTestKit.Step("do_b", RunbookStepKind.Action, "demo.do_b", approval: true),
                ResponderTestKit.Step("verify", RunbookStepKind.Verify, "demo.verify"),
            ],
        };
        Harness h = new(d, new RunbookOptions { AutoRunbookIds = new HashSet<string> { "demo.fix" } });

        RunbookRunState state = (await h.Executor().StartAsync(
            ResponderTestKit.Start(state: Bazaar.Kodo.Evaluator.KodoState.Healthy, definition: d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.AwaitingApproval, state.Phase);
        Assert.Contains(h.Runner.Calls, c => c.StepId == "do_a");
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_b");
        Assert.Equal("do_b", h.Gate.Requests.Single().StepId);
    }
}
