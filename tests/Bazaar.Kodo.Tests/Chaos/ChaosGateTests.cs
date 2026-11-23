using Bazaar.Kodo.Chaos;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Tests.Incidents;
using Bazaar.Kodo.Tests.Responder;

namespace Bazaar.Kodo.Tests.Chaos;

public sealed class ChaosGateTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChaosSettings _on = new() { Enabled = true };

    private static ChaosExperiment Experiment(string id = "exp-1", int blast = 10, TimeSpan? duration = null, TimeSpan? abortWithin = null) =>
        new(id, ChaosFault.KillModule, KodoState.Degraded, TimeSpan.FromMinutes(2), duration ?? TimeSpan.FromMinutes(5), blast, abortWithin ?? TimeSpan.FromSeconds(5), [ChaosAbortCondition.OwnerAbort]);

    private static ChaosPlan Plan(string target = "sandbox:clone-1", params ChaosExperiment[] experiments) =>
        new("plan-1", target, experiments.Length == 0 ? [Experiment()] : experiments);

    private static ChaosRequest Request(ChaosPlan? plan = null, bool permission = true, DateTimeOffset? stepUp = null, string actor = "owner") =>
        new(actor, permission, stepUp ?? _now.AddMinutes(-1), plan ?? Plan());

    private static ChaosDecision Decide(ChaosRequest? request = null, ChaosSettings? settings = null, IncidentRecord? incident = null, KodoObservation? observation = null, bool unknownState = false) =>
        ChaosGate.Evaluate(settings ?? _on, request ?? Request(), incident, unknownState ? null : observation ?? new KodoObservation(), _now);

    private static async Task<IncidentRecord> OpenIncidentAsync()
    {
        IncidentHarness h = new();
        h.Planner = new IncidentRecoveryPlanner(ResponderTestKit.Catalogue(), new IncidentOptions { CauseRunbooks = new Dictionary<string, IReadOnlyList<string>> { ["host.disk.free"] = ["fix.disk_space"] } });
        return (await h.Controller().OpenAsync(IncidentHarness.AutoOpen(IncidentTrigger.CriticalProbeRed, "host.disk.free:data"), CancellationToken.None)).Incident!;
    }

    [Fact]
    public void OffByDefaultAndAllowedWhenEverythingHolds()
    {
        Assert.False(new ChaosSettings().Enabled);
        ChaosDecision off = Decide(settings: new ChaosSettings());
        Assert.False(off.Allowed);
        Assert.Equal([ChaosCauses.Disabled], off.Codes);

        ChaosDecision ok = Decide();
        Assert.True(ok.Allowed);
        Assert.Equal([ChaosCauses.Allowed], ok.Codes);
    }

    [Fact]
    public void PermissionAndFreshStepUpAreBothRequired()
    {
        Assert.Contains(ChaosCauses.NoPermission, Decide(Request(permission: false)).Codes);
        Assert.Contains(ChaosCauses.StepUpMissing, ChaosGate.Evaluate(_on, new ChaosRequest("o", true, null, Plan()), null, new KodoObservation(), _now).Codes);
        Assert.Contains(ChaosCauses.StepUpStale, Decide(Request(stepUp: _now.AddMinutes(-6))).Codes);
        Assert.Contains(ChaosCauses.StepUpStale, Decide(Request(stepUp: _now.AddMinutes(1))).Codes);
    }

    [Fact]
    public async Task AnOpenIncidentRefusesAndStillRefusesForEveryPhaseButClosed()
    {
        IncidentRecord incident = await OpenIncidentAsync();
        Assert.Contains(ChaosCauses.IncidentOpen, Decide(incident: incident).Codes);
        Assert.DoesNotContain(ChaosCauses.IncidentOpen, Decide(incident: incident with { Phase = IncidentPhase.Closed }).Codes);
        Assert.False(IncidentFreeze.Check(incident, FrozenAction.ChaosEnable, null, new IncidentOptions()).Allowed);
    }

    [Theory]
    [InlineData(nameof(KodoObservation.CriticalRed))]
    [InlineData(nameof(KodoObservation.CriticalUnknown))]
    [InlineData(nameof(KodoObservation.IntegrityOrContractFailure))]
    [InlineData(nameof(KodoObservation.CrashLoop))]
    [InlineData(nameof(KodoObservation.IncidentActive))]
    public void AnyCriticalStateRefuses(string flag)
    {
        KodoObservation observation = flag switch
        {
            nameof(KodoObservation.CriticalRed) => new() { CriticalRed = true },
            nameof(KodoObservation.CriticalUnknown) => new() { CriticalUnknown = true },
            nameof(KodoObservation.IntegrityOrContractFailure) => new() { IntegrityOrContractFailure = true },
            nameof(KodoObservation.CrashLoop) => new() { CrashLoop = true },
            _ => new() { IncidentActive = true },
        };
        Assert.Contains(ChaosCauses.CriticalState, Decide(observation: observation).Codes);
    }

    [Fact]
    public void AnUnknownStateRefuses()
    {
        Assert.Contains(ChaosCauses.StateUnknown, Decide(unknownState: true).Codes);
    }

    [Theory]
    [InlineData("sandbox:clone-1", ChaosTargetClass.Sandbox)]
    [InlineData("prod", ChaosTargetClass.Production)]
    [InlineData("production-eu", ChaosTargetClass.Production)]
    [InlineData("Prod:main", ChaosTargetClass.Production)]
    [InlineData("live.main", ChaosTargetClass.Production)]
    [InlineData("codex-alchemist", ChaosTargetClass.Unknown)]
    [InlineData("sandbox:", ChaosTargetClass.Unknown)]
    [InlineData("sandbox:UPPER", ChaosTargetClass.Unknown)]
    [InlineData("sandbox:a/../b", ChaosTargetClass.Unknown)]
    [InlineData("sandbox:a b", ChaosTargetClass.Unknown)]
    [InlineData("sandbox:x\nprod", ChaosTargetClass.Unknown)]
    [InlineData("", ChaosTargetClass.Unknown)]
    [InlineData(null, ChaosTargetClass.Unknown)]
    public void TargetsAreClassifiedByIdentifierShape(string? id, ChaosTargetClass expected)
    {
        Assert.Equal(expected, ChaosTargetClassifier.Classify(id, null));
    }

    [Fact]
    public void OnlySandboxTargetsPassAndKnownProductionIdsAlwaysFail()
    {
        foreach (string target in new[] { "prod", "codex-alchemist", "sandbox:", "" })
        {
            Assert.Contains(ChaosCauses.TargetNotSandbox, Decide(Request(Plan(target))).Codes);
        }

        ChaosSettings settings = new() { Enabled = true, KnownProductionIds = new HashSet<string>(["sandbox:oops"], StringComparer.Ordinal) };
        Assert.Contains(ChaosCauses.TargetNotSandbox, Decide(Request(Plan("sandbox:oops")), settings).Codes);
    }

    [Fact]
    public void PlansAreBoundedInDurationBlastRadiusSizeAndAbortDeadline()
    {
        Assert.Contains(ChaosCauses.DurationExceeded, Decide(Request(Plan("sandbox:a", Experiment(duration: TimeSpan.FromMinutes(16))))).Codes);
        Assert.Contains(ChaosCauses.DurationExceeded, Decide(Request(Plan("sandbox:a", Experiment(duration: TimeSpan.Zero)))).Codes);
        Assert.Contains(ChaosCauses.BlastRadiusExceeded, Decide(Request(Plan("sandbox:a", Experiment(blast: 26)))).Codes);
        Assert.Contains(ChaosCauses.BlastRadiusExceeded, Decide(Request(Plan("sandbox:a", Experiment(blast: 0)))).Codes);
        Assert.Contains(ChaosCauses.AbortDeadlineInvalid, Decide(Request(Plan("sandbox:a", Experiment(abortWithin: TimeSpan.FromSeconds(11))))).Codes);
        Assert.Contains(ChaosCauses.PlanSize, Decide(Request(new ChaosPlan("p", "sandbox:a", []))).Codes);
        ChaosExperiment[] tooMany = [.. Enumerable.Range(0, 11).Select(i => Experiment($"e{i}", duration: TimeSpan.FromMinutes(1)))];
        Assert.Contains(ChaosCauses.PlanSize, Decide(Request(Plan("sandbox:a", tooMany))).Codes);
        ChaosExperiment[] longRun = [.. Enumerable.Range(0, 5).Select(i => Experiment($"e{i}", duration: TimeSpan.FromMinutes(15)))];
        Assert.Contains(ChaosCauses.TotalDurationExceeded, Decide(Request(Plan("sandbox:a", longRun))).Codes);
        Assert.Contains(ChaosCauses.ExperimentInvalid, Decide(Request(Plan("sandbox:a", Experiment("dup"), Experiment("dup")))).Codes);
        Assert.Contains(ChaosCauses.PlanInvalid, Decide(Request(new ChaosPlan("bad id", "sandbox:a", [Experiment()]))).Codes);
    }

    [Fact]
    public void SettingsMayTightenButNeverLoosenPastTheCeilings()
    {
        Assert.True(new ChaosSettings { Enabled = true, MaxBlastRadiusPercent = 5 }.IsValid());
        foreach (ChaosSettings loose in new[]
        {
            new ChaosSettings { Enabled = true, MaxBlastRadiusPercent = 51 },
            new ChaosSettings { Enabled = true, MaxExperimentDuration = TimeSpan.FromHours(2), MaxTotalDuration = TimeSpan.FromHours(3) },
            new ChaosSettings { Enabled = true, MaxTotalDuration = TimeSpan.FromHours(5) },
            new ChaosSettings { Enabled = true, MaxExperiments = 51 },
            new ChaosSettings { Enabled = true, MaxStepUpAge = TimeSpan.FromHours(1) },
        })
        {
            Assert.False(loose.IsValid());
            Assert.Equal([ChaosCauses.SettingsInvalid], Decide(settings: loose).Codes);
        }
    }

    [Fact]
    public void AllRefusalReasonsAreCollectedInAFixedOrder()
    {
        ChaosDecision d = Decide(Request(Plan("prod", Experiment(blast: 90)), permission: false), observation: new KodoObservation { CriticalRed = true });
        Assert.False(d.Allowed);
        Assert.Equal([ChaosCauses.NoPermission, ChaosCauses.CriticalState, ChaosCauses.TargetNotSandbox, ChaosCauses.BlastRadiusExceeded], d.Codes);
        Assert.Equal([ChaosCauses.PlanInvalid], ChaosGate.Evaluate(_on, null, null, new KodoObservation(), _now).Codes);
    }

    [Fact]
    public void TheAuditRecordIsStableRedactsBadIdentifiersAndNeverCarriesFreeText()
    {
        ChaosRequest request = Request();
        ChaosDecision decision = Decide(request);
        ChaosAuditRecord a = ChaosAuditRecord.Create(_on, request, decision, _now);
        ChaosAuditRecord b = ChaosAuditRecord.Create(_on, request, decision, _now);
        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(64, a.Digest.Length);
        Assert.Equal(ChaosTargetClass.Sandbox, a.TargetClass);
        Assert.True(a.Allowed);

        ChaosRequest hostile = Request(new ChaosPlan("p\nq", "prod secret=hunter2", [Experiment()]), actor: "someone with spaces");
        ChaosAuditRecord r = ChaosAuditRecord.Create(_on, hostile, Decide(hostile), _now);
        Assert.Equal(ChaosAuditRecord.Redacted, r.ActorId);
        Assert.Equal(ChaosAuditRecord.Redacted, r.PlanId);
        Assert.Equal(ChaosAuditRecord.Redacted, r.TargetId);
        Assert.False(r.Allowed);
        Assert.NotEqual(a.Digest, r.Digest);

        ChaosAuditRecord none = ChaosAuditRecord.Create(_on, null, new ChaosDecision(false, [ChaosCauses.PlanInvalid]), _now);
        Assert.Equal(ChaosAuditRecord.Redacted, none.ActorId);
    }
}
