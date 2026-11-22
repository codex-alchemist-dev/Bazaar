using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentFreezeTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static async Task<IncidentRecord> OpenAsync(IncidentHarness h)
    {
        h.Planner = new IncidentRecoveryPlanner(
            Bazaar.Kodo.Tests.Responder.ResponderTestKit.Catalogue(),
            new IncidentOptions { CauseRunbooks = new Dictionary<string, IReadOnlyList<string>> { ["host.disk.free"] = ["fix.disk_space"] } });
        return (await h.Controller().OpenAsync(IncidentHarness.AutoOpen(IncidentTrigger.CriticalProbeRed, "host.disk.free:data"), CancellationToken.None)).Incident!;
    }

    [Fact]
    public async Task AnOpenIncidentShowsFrozenBesideTheUnderlyingState()
    {
        IncidentHarness h = new();
        IncidentRecord incident = await OpenAsync(h);

        KodoObservation observation = IncidentFreeze.Observe(new KodoObservation { CriticalRed = true }, incident);
        KodoEvaluation result = KodoEvaluator.Evaluate(KodoEvaluator.Initial, observation, _now, new KodoEvaluatorSettings());

        Assert.Equal(KodoState.Frozen, result.State);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public async Task EveryPhaseButClosedHoldsTheFreeze()
    {
        IncidentHarness h = new();
        IncidentRecord incident = await OpenAsync(h);
        foreach (IncidentPhase phase in Enum.GetValues<IncidentPhase>())
        {
            Assert.Equal(phase != IncidentPhase.Closed, IncidentFreeze.IsFrozen(incident with { Phase = phase }));
        }

        Assert.False(IncidentFreeze.IsFrozen(null));
        KodoObservation unfrozen = IncidentFreeze.Observe(new KodoObservation { IncidentActive = true }, null);
        Assert.False(unfrozen.IncidentActive);
    }

    [Fact]
    public async Task AutomaticActionsAreRefusedWhileFrozenAndReadsAreNot()
    {
        IncidentHarness h = new();
        IncidentRecord incident = await OpenAsync(h);
        IncidentOptions options = new() { ConfigAllowlist = new HashSet<string>(["kodo.alert.escalation_min.critical"], StringComparer.Ordinal) };

        Assert.True(IncidentFreeze.Check(incident, FrozenAction.ReadOnly, null, options).Allowed);
        Assert.True(IncidentFreeze.Check(incident, FrozenAction.ConfigChange, "kodo.alert.escalation_min.critical", options).Allowed);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.ConfigChange, "other.key", options).Allowed);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.ConfigChange, null, options).Allowed);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.AutomaticUpdate, null, options).Allowed);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.AgentHighImpact, null, options).Allowed);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.ModuleInstall, null, options).Allowed);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.ChaosEnable, null, options).Allowed);
        Assert.Equal("automatic_runbook_frozen", IncidentFreeze.Check(incident, FrozenAction.AutomaticRunbook, "fix.disk_space", options).Code);
    }

    [Fact]
    public async Task OnlyTheIncidentsOwnRunbooksMayStart()
    {
        IncidentHarness h = new();
        IncidentRecord incident = await OpenAsync(h);
        IncidentOptions options = new();

        Assert.Equal("incident_runbook", IncidentFreeze.Check(incident, FrozenAction.RunbookStart, "fix.disk_space", options).Code);
        Assert.Equal("runbook_not_in_incident_plan", IncidentFreeze.Check(incident, FrozenAction.RunbookStart, "fix.cert_renew", options).Code);
        Assert.Equal("runbook_not_in_incident_plan", IncidentFreeze.Check(incident, FrozenAction.RunbookStart, null, options).Code);
    }

    [Fact]
    public void WithoutAnIncidentEverythingIsAllowed()
    {
        foreach (FrozenAction action in Enum.GetValues<FrozenAction>())
        {
            FreezeDecision decision = IncidentFreeze.Check(null, action, null, new IncidentOptions());
            Assert.True(decision.Allowed);
            Assert.Equal("no_incident", decision.Code);
        }
    }

    [Fact]
    public void AutomaticEntryFloorIsIntegritySplitBrainReplicationAndALongRedProbe()
    {
        IncidentOptions options = new();
        Assert.Equal(IncidentTrigger.IntegrityFailure, IncidentAutoEntry.Evaluate(new IncidentSignals(null, true, true, true), _now, options));
        Assert.Equal(IncidentTrigger.SplitBrain, IncidentAutoEntry.Evaluate(new IncidentSignals(null, false, true, true), _now, options));
        Assert.Equal(IncidentTrigger.ReplicationBreak, IncidentAutoEntry.Evaluate(new IncidentSignals(null, false, false, true), _now, options));
        Assert.Null(IncidentAutoEntry.Evaluate(new IncidentSignals(null, false, false, false), _now, options));
    }

    [Fact]
    public void ARedProbeOpensAtTheGraceBoundaryAndASettingOnlyTightensIt()
    {
        IncidentOptions options = new();
        Assert.Null(IncidentAutoEntry.Evaluate(new IncidentSignals(_now - TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1), false, false, false), _now, options));
        Assert.Equal(
            IncidentTrigger.CriticalProbeRed,
            IncidentAutoEntry.Evaluate(new IncidentSignals(_now - TimeSpan.FromMinutes(10), false, false, false), _now, options));

        IncidentOptions tighter = new() { CriticalRedGrace = TimeSpan.FromMinutes(2) };
        Assert.Equal(
            IncidentTrigger.CriticalProbeRed,
            IncidentAutoEntry.Evaluate(new IncidentSignals(_now - TimeSpan.FromMinutes(2), false, false, false), _now, tighter));

        Assert.Throws<ArgumentOutOfRangeException>(() => new IncidentOptions { CriticalRedGrace = TimeSpan.FromMinutes(11) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncidentOptions { CriticalRedGrace = TimeSpan.FromSeconds(30) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncidentOptions { StabilityWindow = TimeSpan.Zero }.Validate());
    }
}
