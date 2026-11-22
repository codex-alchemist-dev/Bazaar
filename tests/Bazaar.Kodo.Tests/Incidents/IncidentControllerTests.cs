using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentControllerTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task AutomaticEntryOpensFreezesAndRecordsWhoWhenWhy()
    {
        IncidentHarness h = new();
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.AutoOpen(), _ct);

        Assert.True(result.Ok);
        IncidentRecord record = result.Incident!;
        Assert.Equal("inc-000001", record.Id);
        Assert.Equal(IncidentPhase.Frozen, record.Phase);
        Assert.Equal(AlertSeverity.Critical, record.Severity);
        Assert.Equal("kodo", record.Opener);
        Assert.True(record.FreezeApplied);
        Assert.Equal(1, h.Freeze.Applied);
        IncidentTransition t = Assert.Single(record.Transitions);
        Assert.Equal((IncidentPhase.Opened, IncidentPhase.Frozen, "kodo", "freeze_applied"), (t.From, t.To, t.Actor, t.Reason));
        Assert.Equal(h.Clock.GetUtcNow(), t.At);
        Assert.Equal(["incident_opened", "incident_transition"], h.Journal.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task ManualEntryNeedsTheEnterControl()
    {
        IncidentHarness h = new();
        h.ApproveAll();
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.ManualOpen(IncidentHarness.Controls(KodoControls.RunbookRun)), _ct);

        Assert.False(result.Ok);
        Assert.Equal("missing_control", result.Code);
        Assert.Empty(h.Store.Files);
        Assert.Empty(h.Gate.Requests);
    }

    [Theory]
    [InlineData(ApprovalDecision.Pending, "approval_pending")]
    [InlineData(ApprovalDecision.Denied, "approval_denied")]
    [InlineData(ApprovalDecision.TimedOut, "approval_timed_out")]
    public async Task ManualEntryWithoutAnApprovedAnswerOpensNothing(ApprovalDecision decision, string code)
    {
        IncidentHarness h = new();
        h.Gate.Decide = _ => decision;
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.ManualOpen(), _ct);

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Empty(h.Store.Files);
        Assert.Equal(0, h.Store.Counter);
        Assert.Equal(0, h.Freeze.Applied);
    }

    [Fact]
    public async Task ManualEntryWithApprovalOpensAndAskedForTheEnterControl()
    {
        IncidentHarness h = new();
        h.ApproveAll();
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.ManualOpen(), _ct);

        Assert.True(result.Ok);
        Assert.Equal("owner", result.Incident!.Opener);
        ApprovalRequest request = Assert.Single(h.Gate.Requests);
        Assert.Equal(KodoControls.IncidentEnter, request.RequiredControl);
        Assert.False(request.RequireStepUp);
    }

    [Fact]
    public async Task EnterStepUpIsAnOptionAndAMissingStepUpRefuses()
    {
        IncidentHarness h = new(new IncidentOptions { EnterRequiresStepUp = true });
        h.ApproveAll();
        h.Gate.StepUp = false;
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.ManualOpen(), _ct);

        Assert.False(result.Ok);
        Assert.Equal("step_up_missing", result.Code);
        Assert.True(Assert.Single(h.Gate.Requests).RequireStepUp);
    }

    [Fact]
    public async Task AGateErrorOrAMismatchedDigestNeverOpens()
    {
        IncidentHarness h = new();
        h.ApproveAll();
        h.Gate.Throws = true;
        Assert.Equal("approval_unavailable", (await h.Controller().OpenAsync(IncidentHarness.ManualOpen(), _ct)).Code);

        h.Gate.Throws = false;
        h.Gate.DigestOverride = "something-else";
        Assert.Equal("approval_mismatch", (await h.Controller().OpenAsync(IncidentHarness.ManualOpen(), _ct)).Code);
        Assert.Empty(h.Store.Files);
    }

    [Fact]
    public async Task OnlyOneIncidentIsActiveAtATime()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord first = (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;

        IncidentResult second = await controller.OpenAsync(IncidentHarness.AutoOpen(IncidentTrigger.SplitBrain, "kodo.split"), _ct);
        Assert.False(second.Ok);
        Assert.Equal("incident_already_active", second.Code);
        Assert.Equal(first.Id, second.Incident!.Id);
    }

    [Fact]
    public async Task InvalidRequestsAreRefusedBeforeAnythingIsWritten()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();

        IncidentOpenRequest bad = IncidentHarness.AutoOpen() with { Title = "line\nbreak" };
        Assert.Equal("invalid_request", (await controller.OpenAsync(bad, _ct)).Code);
        Assert.Equal("invalid_request", (await controller.OpenAsync(IncidentHarness.AutoOpen() with { Cause = "has space" }, _ct)).Code);
        Assert.Equal("invalid_request", (await controller.OpenAsync(IncidentHarness.AutoOpen() with { Categories = ["Not Valid"] }, _ct)).Code);
        Assert.Empty(h.Store.Files);
    }

    [Fact]
    public async Task EveryValidMoveWorksAndEveryInvalidMoveIsRefused()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;

        foreach (IncidentPhase to in Enum.GetValues<IncidentPhase>().Where(p => p != IncidentPhase.Investigating && p != IncidentPhase.Opened))
        {
            IncidentResult refused = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, to), _ct);
            Assert.False(refused.Ok);
            Assert.Equal("invalid_transition", refused.Code);
        }

        IncidentResult investigating = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct);
        Assert.True(investigating.Ok);
        Assert.Equal(IncidentPhase.Investigating, investigating.Incident!.Phase);
        Assert.Equal("owner", investigating.Incident.Transitions[^1].Actor);
        Assert.Equal("step", investigating.Incident.Transitions[^1].Reason);

        IncidentResult recovering = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Recovering), _ct);
        Assert.True(recovering.Ok);
        IncidentResult back = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct);
        Assert.True(back.Ok);
    }

    [Fact]
    public async Task UnknownIncidentAndBadActorAreRefused()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        Assert.Equal("unknown_incident", (await controller.AdvanceAsync(IncidentHarness.Advance("inc-000099", IncidentPhase.Investigating), _ct)).Code);
        IncidentRecord record = (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;
        IncidentAdvanceRequest bad = IncidentHarness.Advance(record.Id, IncidentPhase.Investigating) with { Actor = "not valid" };
        Assert.Equal("invalid_request", (await controller.AdvanceAsync(bad, _ct)).Code);
    }

    [Fact]
    public async Task ResolvingNeedsTheProbesGreenForTheStabilityWindow()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Investigating);
        DateTimeOffset now = h.Clock.GetUtcNow();

        IncidentResult none = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Resolved), _ct);
        IncidentResult tooShort = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: now - TimeSpan.FromMinutes(4)), _ct);
        IncidentResult future = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: now + TimeSpan.FromMinutes(1)), _ct);
        Assert.All([none, tooShort, future], r => Assert.Equal("probes_not_stable", r.Code));

        IncidentResult ok = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: now - TimeSpan.FromMinutes(5)), _ct);
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task ExitWithoutTheControlIsRefusedBeforeTheGateIsAsked()
    {
        IncidentHarness h = new();
        h.ApproveAll();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);

        IncidentResult result = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.RunbookRun)), _ct);

        Assert.False(result.Ok);
        Assert.Equal("missing_control", result.Code);
        Assert.Empty(h.Gate.Requests);
        Assert.Equal(0, h.Freeze.Released);
    }

    [Theory]
    [InlineData(ApprovalDecision.Pending, "approval_pending")]
    [InlineData(ApprovalDecision.Denied, "approval_denied")]
    [InlineData(ApprovalDecision.TimedOut, "approval_timed_out")]
    public async Task ExitWithoutAnApprovedAnswerStaysOpenAndFrozen(ApprovalDecision decision, string code)
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);
        h.Gate.Decide = _ => decision;

        IncidentResult result = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal(IncidentPhase.Resolved, result.Incident!.Phase);
        Assert.True(IncidentFreeze.IsFrozen(result.Incident));
        Assert.Equal(0, h.Freeze.Released);
        ApprovalRequest request = Assert.Single(h.Gate.Requests);
        Assert.Equal(KodoControls.IncidentExit, request.RequiredControl);
        Assert.True(request.RequireStepUp);
    }

    [Fact]
    public async Task TimeNeverClosesAnIncident()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);
        h.Gate.Decide = _ => ApprovalDecision.TimedOut;

        h.Clock.Advance(TimeSpan.FromDays(30));
        IncidentResult after = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);

        Assert.False(after.Ok);
        Assert.Equal(IncidentPhase.Resolved, (await controller.GetAsync(record.Id, _ct))!.Phase);
        Assert.NotNull(await controller.ActiveAsync(_ct));

        // Nothing else moves it either: recovery reapplies the freeze and leaves the phase alone.
        await controller.RecoverAsync(_ct);
        Assert.Equal(IncidentPhase.Resolved, (await controller.GetAsync(record.Id, _ct))!.Phase);
    }

    [Fact]
    public async Task ExitWithoutStepUpOrWithAMismatchedDigestIsRefused()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);
        h.ApproveAll();
        IncidentAdvanceRequest close = IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit));

        h.Gate.StepUp = false;
        Assert.Equal("step_up_missing", (await controller.AdvanceAsync(close, _ct)).Code);

        h.Gate.StepUp = true;
        h.Gate.DigestOverride = "other";
        Assert.Equal("approval_mismatch", (await controller.AdvanceAsync(close, _ct)).Code);

        h.Gate.DigestOverride = null;
        h.Gate.Throws = true;
        Assert.Equal("approval_unavailable", (await controller.AdvanceAsync(close, _ct)).Code);
        Assert.Equal(IncidentPhase.Resolved, (await controller.GetAsync(record.Id, _ct))!.Phase);
    }

    [Fact]
    public async Task AnApprovedSteppedUpExitClosesAndLiftsTheFreeze()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);
        h.ApproveAll();

        IncidentResult closed = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);

        Assert.True(closed.Ok);
        Assert.Equal(IncidentPhase.Closed, closed.Incident!.Phase);
        Assert.False(IncidentFreeze.IsFrozen(closed.Incident));
        Assert.True(closed.Incident.FreezeReleased);
        Assert.Equal(1, h.Freeze.Released);
        Assert.NotNull(closed.ApprovalId);
        Assert.Equal(closed.ApprovalId, closed.Incident.Transitions[^1].ApprovalId);
        Assert.Null(await controller.ActiveAsync(_ct));
        Assert.Equal("invalid_transition", (await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct)).Code);
    }

    [Fact]
    public async Task ADeniedExitIsJournalled()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);
        h.Gate.Decide = _ => ApprovalDecision.Denied;

        await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);

        Assert.Contains(h.Journal.Entries, e => e.Kind == IncidentJournalKinds.ApprovalDenied);
    }

    [Fact]
    public async Task ARejectedResolutionCanReopenInvestigation()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);

        IncidentResult back = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct);

        Assert.True(back.Ok);
        Assert.True(IncidentFreeze.IsFrozen(back.Incident));
    }

    [Fact]
    public async Task AFailedFreezeLeavesTheIncidentOpenedAndRecoveryCompletesIt()
    {
        IncidentHarness h = new();
        h.Freeze.FailApply = true;
        IncidentController controller = h.Controller();

        IncidentResult first = await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct);
        Assert.False(first.Ok);
        Assert.Equal("freeze_pending", first.Code);
        Assert.Equal(IncidentPhase.Opened, first.Incident!.Phase);
        Assert.True(IncidentFreeze.IsFrozen(first.Incident));

        h.Freeze.FailApply = false;
        IncidentResult recovered = Assert.Single(await controller.RecoverAsync(_ct));
        Assert.True(recovered.Ok);
        Assert.Equal(IncidentPhase.Frozen, recovered.Incident!.Phase);
        Assert.True(recovered.Incident.FreezeApplied);
    }

    [Fact]
    public async Task RestartResumesMidIncidentFromTheSavedRecord()
    {
        IncidentHarness h = new();
        IncidentRecord record = await h.WalkAsync(h.Controller(), IncidentPhase.Recovering);
        h.Clock.Advance(TimeSpan.FromMinutes(20));

        // A fresh controller over the same store stands in for a restarted process.
        IncidentController restarted = h.Controller();
        IncidentRecord? active = await restarted.ActiveAsync(_ct);
        Assert.Equal(record.Id, active!.Id);
        Assert.Equal(IncidentPhase.Recovering, active.Phase);
        Assert.True(IncidentFreeze.IsFrozen(active));

        IncidentResult resumed = Assert.Single(await restarted.RecoverAsync(_ct));
        Assert.True(resumed.Ok);
        Assert.Equal(2, h.Freeze.Applied);

        IncidentResult resolved = await restarted.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: h.Clock.GetUtcNow() - TimeSpan.FromMinutes(6)), _ct);
        Assert.True(resolved.Ok);
        Assert.Equal(record.Transitions.Count + 1, resolved.Incident!.Transitions.Count);
    }

    [Fact]
    public async Task ARestartAfterCloseFinishesALiftThatFailed()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Resolved);
        h.ApproveAll();
        h.Freeze.FailRelease = true;

        IncidentResult closed = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);
        Assert.True(closed.Ok);
        Assert.Equal("freeze_release_pending", closed.Code);
        Assert.False(closed.Incident!.FreezeReleased);

        h.Freeze.FailRelease = false;
        IncidentResult lifted = Assert.Single(await h.Controller().RecoverAsync(_ct));
        Assert.True(lifted.Incident!.FreezeReleased);
        Assert.Equal(1, h.Freeze.Released);
        Assert.Empty(await h.Store.ListUnsettledAsync(_ct));
    }

    [Fact]
    public async Task AJournalFailureRefusesTheChangeAndNothingMoves()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;
        h.Journal.Fail = true;

        IncidentResult result = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct);

        Assert.False(result.Ok);
        Assert.Equal("journal_unavailable", result.Code);
        Assert.Equal(IncidentPhase.Frozen, (await controller.GetAsync(record.Id, _ct))!.Phase);
    }

    [Fact]
    public async Task AStoreFailureRefusesTheChange()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;
        h.Store.FailSaves = true;

        IncidentResult result = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct);

        Assert.Equal("state_not_saved", result.Code);
        Assert.Equal(IncidentPhase.Frozen, (await controller.GetAsync(record.Id, _ct))!.Phase);
    }

    [Fact]
    public async Task IncidentNumbersAreSequentialAndNeverReused()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        h.Store.FailSaves = true;
        Assert.Equal("state_not_saved", (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Code);
        h.Store.FailSaves = false;

        IncidentRecord record = (await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;
        Assert.Equal("inc-000002", record.Id);
    }

    [Fact]
    public async Task AFullTimelineStillLeavesRoomToResolveAndClose()
    {
        IncidentHarness h = new();
        IncidentController controller = h.Controller();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Investigating);
        IncidentResult result = new(true, "ok", record);
        while (true)
        {
            result = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Recovering), _ct);
            if (!result.Ok)
            {
                break;
            }

            result = await controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct);
            if (!result.Ok)
            {
                break;
            }
        }

        Assert.Equal("timeline_full", result.Code);
        Assert.Equal(IncidentRecord.MaxTransitions - 2, (await controller.GetAsync(record.Id, _ct))!.Transitions.Count);

        h.ApproveAll();
        IncidentResult resolved = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: h.Clock.GetUtcNow() - TimeSpan.FromHours(1)), _ct);
        Assert.True(resolved.Ok);
        IncidentResult closed = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);
        Assert.True(closed.Ok);
        Assert.Equal(IncidentRecord.MaxTransitions, closed.Incident!.Transitions.Count);
    }
}
