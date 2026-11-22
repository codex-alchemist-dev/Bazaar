using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Responder;

public sealed class RunbookExecutorTests
{
    [Fact]
    public async Task HappyPathRunsEveryStepInOrderAndJournalsBeforeAndAfter()
    {
        Harness h = new();
        h.ApproveAll();

        RunbookStartResult result = await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(RunbookPhase.Succeeded, result.State!.Phase);
        Assert.Equal(["pre", "do_a", "do_b", "verify"], h.Runner.Calls.Select(c => c.StepId));
        Assert.Equal("run1/do_a", h.Runner.Calls[1].IdempotencyKey);
        string[] kinds = h.Journal.Kinds.ToArray();
        Assert.Equal("run_started", kinds[0]);
        Assert.Equal("run_finished", kinds[^1]);
        int startedA = Array.IndexOf(kinds, "step_started:do_a");
        int doneA = Array.IndexOf(kinds, "step_succeeded:do_a");
        int startedB = Array.IndexOf(kinds, "step_started:do_b");
        Assert.True(startedA < doneA && doneA < startedB);
        Assert.Equal(["ap-do_a", "ap-do_b"], result.State.Approvals.Select(a => a.ApprovalId));
    }

    [Fact]
    public async Task ReadOnlyLowRiskRunBecomesUnattendedOnlyWhenTheOperatorNamesIt()
    {
        RunbookDefinition low = ResponderTestKit.Demo(RunbookRisk.Low, RunbookApprovalTier.None);

        Harness unnamed = new(low);
        RunbookStartResult gated = await unnamed.Executor().StartAsync(ResponderTestKit.Start(state: KodoState.Healthy, definition: low), CancellationToken.None);
        Assert.Equal(RunbookPhase.AwaitingApproval, gated.State!.Phase);

        Harness named = new(low, new RunbookOptions { AutoRunbookIds = new HashSet<string> { "demo.fix" } });
        RunbookStartResult auto = await named.Executor().StartAsync(ResponderTestKit.Start(state: KodoState.Healthy, definition: low), CancellationToken.None);
        Assert.Equal(RunbookPhase.Succeeded, auto.State!.Phase);
        Assert.Empty(named.Gate.Requests);

        Harness degraded = new(low, new RunbookOptions { AutoRunbookIds = new HashSet<string> { "demo.fix" } });
        RunbookStartResult notHealthy = await degraded.Executor().StartAsync(ResponderTestKit.Start(state: KodoState.Degraded, definition: low), CancellationToken.None);
        Assert.Equal(RunbookPhase.AwaitingApproval, notHealthy.State!.Phase);
    }

    [Fact]
    public async Task TaintedInputsAlwaysNeedApprovalEvenForAnAutoRunbook()
    {
        RunbookDefinition low = ResponderTestKit.Demo(RunbookRisk.Low, RunbookApprovalTier.None);
        Harness h = new(low, new RunbookOptions { AutoRunbookIds = new HashSet<string> { "demo.fix" } });

        RunbookStartResult result = await h.Executor().StartAsync(ResponderTestKit.Start(tainted: true, state: KodoState.Healthy, definition: low), CancellationToken.None);

        Assert.Equal(RunbookPhase.AwaitingApproval, result.State!.Phase);
        Assert.True(h.Gate.Requests.Single().Tainted);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task FailingActionStepRollsBackAndEndsRolledBack()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Script("do_b", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.RolledBack, state.Phase);
        Assert.Equal("step_failed", state.Cause);
        Assert.Equal(["pre", "do_a", "do_b", "undo_a"], h.Runner.Calls.Select(c => c.StepId));
        Assert.Contains("rollback_started", h.Journal.Kinds);
        Assert.Contains("ap-rollback-undo_a", state.Approvals.Select(a => a.ApprovalId));
    }

    [Fact]
    public async Task FailedVerifyRollsBack()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Script("verify", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.RolledBack, state.Phase);
        Assert.Equal("undo_a", h.Runner.Calls[^1].StepId);
    }

    [Fact]
    public async Task FailedRollbackStepStopsAsNeedsHuman()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Script("do_b", RunbookStepOutcome.Failed);
        h.Runner.Script("undo_a", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("rollback_failed", state.Cause);
    }

    [Fact]
    public async Task FailedPreconditionChangesNothing()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Script("pre", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.Failed, state.Phase);
        Assert.Equal("precondition_failed", state.Cause);
        Assert.Equal(["pre"], h.Runner.Calls.Select(c => c.StepId));
    }

    [Fact]
    public async Task FailureWithActionsDoneAndNoRollbackDefinedNeedsAHuman()
    {
        RunbookDefinition noRollback = ResponderTestKit.Demo() with { Rollback = [] };
        Harness h = new(noRollback);
        h.ApproveAll();
        h.Runner.Script("verify", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(definition: noRollback), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("no_rollback_defined", state.Cause);
    }

    [Fact]
    public async Task TransientFailuresRetryWithBackoffAndAreBounded()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Script("verify", RunbookStepOutcome.Transient, RunbookStepOutcome.Transient, RunbookStepOutcome.Succeeded);
        RunbookExecutor executor = h.Executor();

        RunbookRunState state = (await executor.StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;
        Assert.Equal(RunbookPhase.Running, state.Phase);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(5), state.NotBefore);

        // Too early: nothing runs.
        int calls = h.Runner.Calls.Count;
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(calls, h.Runner.Calls.Count);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(10), state.NotBefore);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.Succeeded, state.Phase);
    }

    [Fact]
    public async Task RetriesNeverExceedTheCapEvenWhenTheStepAsksForMore()
    {
        RunbookDefinition d = ResponderTestKit.Demo() with
        {
            Preconditions = [ResponderTestKit.Step("pre", RunbookStepKind.Check, "demo.pre", attempts: 5)],
        };
        Harness h = new(d, new RunbookOptions { MaxAttemptsCap = 2 });
        h.ApproveAll();
        h.Runner.Script("pre", RunbookStepOutcome.Transient, RunbookStepOutcome.Transient, RunbookStepOutcome.Transient);
        RunbookExecutor executor = h.Executor();

        await executor.StartAsync(ResponderTestKit.Start(definition: d), CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        RunbookRunState state = (await executor.ResumeAsync("run1", CancellationToken.None))!;

        Assert.Equal(RunbookPhase.Failed, state.Phase);
        Assert.Equal(2, h.Runner.Calls.Count(c => c.StepId == "pre"));
    }

    [Fact]
    public async Task ActionTimeoutIsUnknownAndStopsForAHuman()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Script("do_a", RunbookStepOutcome.TimedOut);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("action_timed_out", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "undo_a");
    }

    [Fact]
    public async Task RunnerExceptionOnAReadOnlyStepFailsTheRunCleanly()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Throw = new InvalidOperationException("boom");

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.Failed, state.Phase);
        Assert.Equal(["pre"], h.Runner.Calls.Select(c => c.StepId));
    }

    [Fact]
    public async Task RunnerExceptionOnAnActionIsUnknownAndStopsForAHuman()
    {
        Harness h = new();
        h.ApproveAll();
        h.Runner.Throw = new InvalidOperationException("boom");
        h.Runner.ThrowOn = "do_a";

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("action_timed_out", state.Cause);
    }

    [Fact]
    public async Task WaitStepWaitsWithoutThreadsAndContinuesAfterTheDelay()
    {
        RunbookDefinition d = ResponderTestKit.Demo() with
        {
            Steps =
            [
                ResponderTestKit.Step("do_a", RunbookStepKind.Action, "demo.do_a"),
                ResponderTestKit.Step("settle", RunbookStepKind.Wait, "demo.settle", waitFor: TimeSpan.FromSeconds(30)),
                ResponderTestKit.Step("verify", RunbookStepKind.Verify, "demo.verify"),
            ],
        };
        Harness h = new(d);
        h.ApproveAll();
        RunbookExecutor executor = h.Executor();

        RunbookRunState state = (await executor.StartAsync(ResponderTestKit.Start(definition: d), CancellationToken.None)).State!;
        Assert.Equal(RunbookPhase.Running, state.Phase);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "verify");

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        state = (await executor.ResumeAsync("run1", CancellationToken.None))!;
        Assert.Equal(RunbookPhase.Succeeded, state.Phase);
    }

    [Fact]
    public async Task NotifyFailureIsRecordedAndDoesNotRollBackAGoodFix()
    {
        RunbookDefinition d = ResponderTestKit.Demo() with
        {
            Steps =
            [
                ResponderTestKit.Step("do_a", RunbookStepKind.Action, "demo.do_a"),
                ResponderTestKit.Step("verify", RunbookStepKind.Verify, "demo.verify"),
                ResponderTestKit.Step("tell", RunbookStepKind.Notify, "demo.tell"),
            ],
        };
        Harness h = new(d);
        h.ApproveAll();
        h.Runner.Script("tell", RunbookStepOutcome.Failed);

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(definition: d), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.Succeeded, state.Phase);
        Assert.Contains("step_failed:tell", h.Journal.Kinds);
    }

    [Fact]
    public async Task JournalFailureBeforeAStepStopsTheRunBeforeTheStepRuns()
    {
        Harness h = new();
        h.ApproveAll();
        h.Journal.FailWhen = e => e.Kind == "step_started" && e.StepId == "do_a";

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("journal_failed", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task StateSaveFailureBeforeAnActionStopsTheRunBeforeTheActionRuns()
    {
        Harness h = new();
        h.ApproveAll();
        h.Store.FailAfterSaves = 5;

        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
        Assert.Equal("state_not_saved", state.Cause);
        Assert.DoesNotContain(h.Runner.Calls, c => c.StepId == "do_a");
    }

    [Fact]
    public async Task StartThatCannotSaveItsStateRefusesAndRunsNothing()
    {
        Harness h = new();
        h.Store.FailSaves = true;

        RunbookStartResult refused = await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        Assert.False(refused.Started);
        Assert.Equal("state_not_saved", refused.Refusal);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task PreviewShowsActionStepsWithoutRunningAnything()
    {
        Harness h = new();

        IReadOnlyList<RunbookPreview>? preview = await h.Executor().PreviewAsync("demo.fix", 1, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(["do_a", "do_b"], preview!.Select(p => p.StepId));
        Assert.Empty(h.Runner.Calls);
        Assert.Null(await h.Executor().PreviewAsync("nope.fix", 1, new Dictionary<string, string>(), CancellationToken.None));
    }

    [Fact]
    public async Task FinishedRunMakesAContentFreeWebhookEnvelope()
    {
        Harness h = new();
        h.ApproveAll();
        RunbookRunState state = (await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None)).State!;

        Bazaar.Kodo.Export.KodoWebhookEnvelope envelope = RunbookWebhook.Finished(state);

        Assert.Equal("kodo.runbook.finished", envelope.EventType);
        string body = System.Text.Encoding.UTF8.GetString(envelope.Body);
        Assert.Contains("\"phase\":\"succeeded\"", body);
    }
}
