using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Responder;
using Bazaar.Kodo.Tests.Responder;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentRecoveryTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private sealed class Rig
    {
        public Rig(RunbookDefinition? definition = null, RunbookOptions? runbookOptions = null)
        {
            Runbooks = new Harness(definition, runbookOptions);
            Incidents = new IncidentHarness();
            Incidents.Planner = new IncidentRecoveryPlanner(
                Runbooks.Catalogue,
                new IncidentOptions
                {
                    CauseRunbooks = new Dictionary<string, IReadOnlyList<string>> { ["demo.cause"] = ["demo.fix", "demo.missing"] },
                });
        }

        public Harness Runbooks { get; }

        public IncidentHarness Incidents { get; }

        public IncidentController Controller { get; set; } = null!;

        public IncidentRecoveryDriver Driver { get; set; } = null!;

        public async Task<IncidentRecord> OpenInvestigatingAsync(string cause = "demo.cause")
        {
            Incidents.Clock.SetUtcNow(Runbooks.Clock.GetUtcNow());
            Controller = Incidents.Controller();
            Driver = new IncidentRecoveryDriver(Controller, Runbooks.Executor());
            IncidentRecord record = (await Controller.OpenAsync(IncidentHarness.AutoOpen(IncidentTrigger.CriticalProbeRed, cause), _ct)).Incident!;
            return (await Controller.AdvanceAsync(IncidentHarness.Advance(record.Id, IncidentPhase.Investigating), _ct)).Incident!;
        }

        public static IncidentRecoveryRequest Request(string id, string runbook = "demo.fix", KodoState underlying = KodoState.Failing, bool tainted = false, string[]? controls = null) =>
            new(
                id,
                runbook,
                "owner",
                IncidentHarness.Controls(controls ?? [KodoControls.RunbookRun]),
                new Dictionary<string, string>(StringComparer.Ordinal),
                tainted,
                underlying);
    }

    [Fact]
    public void TheCauseMapsToShippedFixesTheTriggerFloorAndTheOperatorsList()
    {
        IncidentRecoveryPlanner planner = new(ResponderTestKit.Catalogue(ResponderTestKit.Demo()), new IncidentOptions
        {
            CauseRunbooks = new Dictionary<string, IReadOnlyList<string>> { ["host.disk.free"] = ["demo.fix", "fix.disk_space", "not a valid id"] },
        });

        IReadOnlyList<PlannedRunbook> plan = planner.Plan(IncidentTrigger.CriticalProbeRed, "host.disk.free:data");

        Assert.Equal(["fix.disk_space", "demo.fix"], plan.Select(p => p.RunbookId));
        Assert.False(plan[0].Available);
        Assert.True(plan[1].Available);
        Assert.Equal("Demo fix", plan[1].Title);

        Assert.Equal(["fix.slot_switch"], planner.Plan(IncidentTrigger.IntegrityFailure, "kodo.self.signature_verify").Select(p => p.RunbookId));
        Assert.Equal(["fix.replication_reseed"], planner.Plan(IncidentTrigger.ReplicationBreak, "replication.status").Select(p => p.RunbookId));
        Assert.Empty(planner.Plan(IncidentTrigger.SplitBrain, "kodo.split"));
        Assert.Empty(planner.Plan(IncidentTrigger.CriticalProbeRed, "unknown.probe"));
    }

    [Fact]
    public async Task ThePlanIsSavedWithTheIncidentSoARestartKeepsIt()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();

        Assert.Equal(["demo.fix", "demo.missing"], record.Plan.Select(p => p.RunbookId));
        IncidentRecord? reloaded = await rig.Incidents.Controller().GetAsync(record.Id, _ct);
        Assert.Equal(record.Plan, reloaded!.Plan);

        IReadOnlyList<IncidentChecklistItem> checklist = IncidentRecoveryDriver.Checklist(reloaded);
        Assert.Equal([true, false], checklist.Select(i => i.Available));
        Assert.All(checklist, item => Assert.Null(item.Phase));
    }

    [Fact]
    public async Task StartingAPlannedRunbookRunsItThroughTheResponderAndMovesToRecovering()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();
        rig.Runbooks.ApproveAll();

        IncidentRecoveryResult result = await rig.Driver.StartAsync(Rig.Request(record.Id), _ct);

        Assert.True(result.Started);
        Assert.Equal("inc-000001-r1", result.RunId);
        Assert.Equal(IncidentPhase.Recovering, result.Incident!.Phase);
        Assert.Equal("run_started", result.Incident.Transitions[^1].Reason);
        IncidentRunLink link = Assert.Single(result.Incident.Runs);
        Assert.Equal(RunbookPhase.Succeeded, link.Phase);
        Assert.Equal(["demo.do_a", "demo.do_b"], rig.Runbooks.Runner.Calls.Where(c => c.Kind == RunbookStepKind.Action).Select(c => c.Operation));
        Assert.Equal(RunbookPhase.Succeeded, IncidentRecoveryDriver.Checklist(result.Incident)[0].Phase);
    }

    [Fact]
    public async Task ARunInAnIncidentIsNeverUnattendedEvenForAnAutoRunbook()
    {
        RunbookDefinition low = ResponderTestKit.Demo(RunbookRisk.Low, RunbookApprovalTier.None);
        Rig rig = new(low, new RunbookOptions { AutoRunbookIds = new HashSet<string>(["demo.fix"], StringComparer.Ordinal) });
        IncidentRecord record = await rig.OpenInvestigatingAsync();

        // Healthy would let the Responder run this unattended; the driver passes failing, so the run waits for an approval.
        IncidentRecoveryResult result = await rig.Driver.StartAsync(Rig.Request(record.Id, underlying: KodoState.Healthy), _ct);

        Assert.True(result.Started);
        Assert.NotEmpty(rig.Runbooks.Gate.Requests);
        Assert.Equal(RunbookPhase.AwaitingApproval, result.Incident!.Runs[0].Phase);
        Assert.DoesNotContain(rig.Runbooks.Runner.Calls, call => call.Kind == RunbookStepKind.Action);
    }

    [Fact]
    public async Task RunbooksOutsideThePlanAndPhasesOutsideRecoveryAreRefused()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();

        Assert.Equal("not_in_plan", (await rig.Driver.StartAsync(Rig.Request(record.Id, "fix.cert_renew"), _ct)).Code);
        Assert.Equal("unknown_incident", (await rig.Driver.StartAsync(Rig.Request("inc-000042"), _ct)).Code);
        Assert.Equal("invalid_state", (await rig.Driver.StartAsync(Rig.Request(record.Id, underlying: KodoState.Frozen), _ct)).Code);
        Assert.Empty(rig.Runbooks.Runner.Calls);

        await rig.Controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: rig.Incidents.Clock.GetUtcNow() - TimeSpan.FromHours(1)), _ct);
        Assert.Equal("not_in_recovery_phase", (await rig.Driver.StartAsync(Rig.Request(record.Id), _ct)).Code);
    }

    [Fact]
    public async Task TheResponderStillChecksTheControlAndAnUnsignedRunbookCannotRun()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();

        IncidentRecoveryResult noControl = await rig.Driver.StartAsync(Rig.Request(record.Id, controls: [KodoControls.ViewStatus]), _ct);
        Assert.False(noControl.Started);
        Assert.Equal("missing_control", noControl.Code);

        IncidentRecoveryResult unsigned = await rig.Driver.StartAsync(Rig.Request(record.Id, "demo.missing"), _ct);
        Assert.False(unsigned.Started);
        Assert.Equal("unknown_runbook", unsigned.Code);

        Assert.Equal(IncidentPhase.Investigating, (await rig.Controller.GetAsync(record.Id, _ct))!.Phase);
        Assert.Empty((await rig.Controller.GetAsync(record.Id, _ct))!.Runs);
    }

    [Fact]
    public async Task AnotherRunCannotStartWhileOneIsRecovering()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();

        IncidentRecoveryResult refused = await rig.Driver.StartAsync(Rig.Request(record.Id, underlying: KodoState.Recovering), _ct);

        Assert.False(refused.Started);
        Assert.Equal("recovery_running", refused.Code);
    }

    [Fact]
    public async Task ARefreshFollowsAPendingApprovalAndReturnsToInvestigatingWhenTheRunFails()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();

        IncidentRecoveryResult started = await rig.Driver.StartAsync(Rig.Request(record.Id), _ct);
        Assert.Equal(IncidentPhase.Recovering, started.Incident!.Phase);
        Assert.Equal(RunbookPhase.AwaitingApproval, started.Incident.Runs[0].Phase);

        IReadOnlyList<IncidentChecklistItem> waiting = (await rig.Driver.RefreshAsync(record.Id, _ct))!;
        Assert.Equal(RunbookPhase.AwaitingApproval, waiting[0].Phase);
        Assert.Equal(IncidentPhase.Recovering, (await rig.Controller.GetAsync(record.Id, _ct))!.Phase);

        rig.Runbooks.Gate.Decide = _ => ApprovalDecision.Denied;
        IReadOnlyList<IncidentChecklistItem> denied = (await rig.Driver.RefreshAsync(record.Id, _ct))!;

        Assert.Equal(RunbookPhase.Denied, denied[0].Phase);
        IncidentRecord after = (await rig.Controller.GetAsync(record.Id, _ct))!;
        Assert.Equal(IncidentPhase.Investigating, after.Phase);
        Assert.Equal("run_failed", after.Transitions[^1].Reason);
        Assert.Null(await rig.Driver.RefreshAsync("inc-000042", _ct));
    }

    [Fact]
    public async Task ASuccessfulRunKeepsTheIncidentRecoveringUntilTheProbesAreGreen()
    {
        Rig rig = new();
        IncidentRecord record = await rig.OpenInvestigatingAsync();
        rig.Runbooks.ApproveAll();
        await rig.Driver.StartAsync(Rig.Request(record.Id), _ct);

        await rig.Driver.RefreshAsync(record.Id, _ct);

        Assert.Equal(IncidentPhase.Recovering, (await rig.Controller.GetAsync(record.Id, _ct))!.Phase);
    }
}
