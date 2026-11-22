using Bazaar.Kodo.Chaos;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Tests.Incidents;
using Bazaar.Kodo.Tests.Responder;

namespace Bazaar.Kodo.Tests.Chaos;

public sealed class ChaosAbortMonitorTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChaosExperiment _experiment =
        new("exp-1", ChaosFault.SlowDependency, KodoState.Degraded, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), 10, TimeSpan.FromSeconds(10), []);

    private static ChaosRunState Run(
        KodoObservation? observation = null,
        IncidentRecord? incident = null,
        bool dataLoss = false,
        bool owner = false,
        bool unreadable = false,
        ChaosExperiment? experiment = null) =>
        new(experiment ?? _experiment, _start, incident, unreadable ? null : observation ?? new KodoObservation(), dataLoss, owner);

    [Fact]
    public void CarriesOnWhileNothingIsWrong()
    {
        ChaosAbortDecision d = ChaosAbortMonitor.Check(Run(), _start.AddMinutes(1));
        Assert.False(d.Abort);
        Assert.Null(d.StopBy);
    }

    [Fact]
    public async Task AbortsOnAnIncidentFreezeWithinTheDeadline()
    {
        IncidentHarness h = new();
        h.Planner = new IncidentRecoveryPlanner(ResponderTestKit.Catalogue(), new IncidentOptions { CauseRunbooks = new Dictionary<string, IReadOnlyList<string>> { ["host.disk.free"] = ["fix.disk_space"] } });
        IncidentRecord incident = (await h.Controller().OpenAsync(IncidentHarness.AutoOpen(IncidentTrigger.CriticalProbeRed, "host.disk.free:data"), CancellationToken.None)).Incident!;

        DateTimeOffset now = _start.AddMinutes(1);
        ChaosAbortDecision d = ChaosAbortMonitor.Check(Run(incident: incident), now);
        Assert.True(d.Abort);
        Assert.Equal(ChaosCauses.IncidentOpen, d.Code);
        Assert.Equal(now.AddSeconds(10), d.StopBy);
    }

    [Fact]
    public void AbortsOnAnyCriticalStateAndOnAnUnreadableState()
    {
        Assert.Equal(ChaosCauses.CriticalState, ChaosAbortMonitor.Check(Run(new KodoObservation { CriticalRed = true }), _start.AddSeconds(1)).Code);
        Assert.Equal(ChaosCauses.CriticalState, ChaosAbortMonitor.Check(Run(new KodoObservation { CrashLoop = true }), _start.AddSeconds(1)).Code);
        Assert.Equal(ChaosCauses.StateUnknown, ChaosAbortMonitor.Check(Run(unreadable: true), _start.AddSeconds(1)).Code);
    }

    [Fact]
    public void AbortsOnDataLossOwnerRequestClockRegressionAndExpiry()
    {
        Assert.Equal(ChaosCauses.DataLossInvariant, ChaosAbortMonitor.Check(Run(dataLoss: true), _start.AddSeconds(1)).Code);
        Assert.Equal(ChaosCauses.OwnerAbort, ChaosAbortMonitor.Check(Run(owner: true), _start.AddSeconds(1)).Code);
        Assert.Equal(ChaosCauses.ClockRegressed, ChaosAbortMonitor.Check(Run(), _start.AddSeconds(-1)).Code);
        Assert.False(ChaosAbortMonitor.Check(Run(), _start.AddMinutes(5).AddTicks(-1)).Abort);
        Assert.Equal(ChaosCauses.DurationExceeded, ChaosAbortMonitor.Check(Run(), _start.AddMinutes(5)).Code);
    }

    [Fact]
    public void ADeadlineAboveTheCeilingIsClampedToTenSeconds()
    {
        ChaosExperiment loose = _experiment with { AbortWithin = TimeSpan.FromMinutes(5) };
        DateTimeOffset now = _start.AddSeconds(1);
        Assert.Equal(now.AddSeconds(10), ChaosAbortMonitor.Check(Run(owner: true, experiment: loose), now).StopBy);
    }

    [Fact]
    public void AnInjectorIsOnlyAnInterfaceAndCarriesTheAuditDigest()
    {
        Assert.True(typeof(IChaosInjector).IsInterface);
        ChaosInjection injection = new("run-1", "exp-1", ChaosFault.KillModule, "sandbox:a", 10, TimeSpan.FromMinutes(1), new string('a', 64));
        Assert.Equal(64, injection.AuditDigest.Length);
    }
}
