using Bazaar.Server.OutcomeLedger;
using Bazaar.Server.OutcomeLedger.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.Reputation.Tests;

public class WardOutcomeListenerTests(LedgerDb db) : IClassFixture<LedgerDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class CapturingIntake : IWardTriggerIntake
    {
        public List<(Guid Agent, string Tag, bool[] Window)> Reports { get; } = [];

        public Task<WardOutcome?> ReportOutcomesAsync(Guid agentId, string tag, IReadOnlyList<bool> recent, CancellationToken ct)
        {
            Reports.Add((agentId, tag, [.. recent]));
            return Task.FromResult<WardOutcome?>(null);
        }

        public Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_recorded_failure_reaches_the_ward_with_the_ledger_window_and_a_good_outcome_does_not()
    {
        var agent = Guid.NewGuid().ToString();
        var intake = new CapturingIntake();
        var listener = new WardOutcomeListener(new OutcomeWardWatcher(db.DataSource, intake, new OutcomeWardWatcherOptions()));
        var recorder = new OutcomeRecorder(
            new PgOutcomeLedgerStore(db.DataSource), new OutcomeWeights(), TimeProvider.System, [listener]);

        OutcomeRecord Rec(string key, decimal x, int minute) =>
            new(OutcomeKind.CiFirstRun, "org/r#1", x, key, _t0.AddMinutes(minute), [new OutcomeAttribution(agent, "author", "csharp")]);

        await recorder.RecordAsync(Rec("k:1", 1m, 0), TestContext.Current.CancellationToken);
        Assert.Empty(intake.Reports);

        await recorder.RecordAsync(Rec("k:2", 0m, 1), TestContext.Current.CancellationToken);
        await recorder.RecordAsync(Rec("k:3", 0m, 2), TestContext.Current.CancellationToken);
        await recorder.RecordAsync(Rec("k:3", 0m, 2), TestContext.Current.CancellationToken);

        Assert.Equal(2, intake.Reports.Count);
        Assert.Equal([true, true, false], intake.Reports[1].Window);
    }
}
