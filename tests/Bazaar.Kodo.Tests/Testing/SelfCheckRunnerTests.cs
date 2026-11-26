using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Testing;

public sealed class SelfCheckRunnerTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class StubCheck(string id, Func<ProbeResult> result) : ISelfCheck
    {
        public string Id { get; } = id;

        public int Runs { get; private set; }

        public Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(result());
        }
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ACheckThatNeverRanIsDueAtOnceThenWaitsForItsCadence()
    {
        FakeTimeProvider time = Clock();
        StubCheck check = new("selfcheck.checksums", ProbeResult.Green);
        SelfCheckRunner runner = new([new ScheduledSelfCheck(check, SelfCheckRunner.Daily)], time);

        Assert.Equal(["selfcheck.checksums"], runner.Due());
        Assert.Single(await runner.RunDueAsync(KodoState.Healthy, _ct));

        time.Advance(TimeSpan.FromHours(23));
        Assert.Empty(runner.Due());
        Assert.Empty(await runner.RunDueAsync(KodoState.Healthy, _ct));

        time.Advance(TimeSpan.FromHours(1));
        Assert.Single(await runner.RunDueAsync(KodoState.Healthy, _ct));
        Assert.Equal(2, check.Runs);
    }

    [Fact]
    public async Task EachCheckKeepsItsOwnCadence()
    {
        FakeTimeProvider time = Clock();
        StubCheck daily = new("selfcheck.checksums", ProbeResult.Green);
        StubCheck weekly = new("selfcheck.restore", ProbeResult.Green);
        SelfCheckRunner runner = new(
            [new ScheduledSelfCheck(daily, SelfCheckRunner.Daily), new ScheduledSelfCheck(weekly, SelfCheckRunner.Weekly)],
            time);

        await runner.RunDueAsync(KodoState.Healthy, _ct);
        time.Advance(TimeSpan.FromDays(2));
        IReadOnlyList<SelfCheckRun> runs = await runner.RunDueAsync(KodoState.Healthy, _ct);

        Assert.Equal(["selfcheck.checksums"], runs.Select(r => r.Id));
        Assert.Equal(1, weekly.Runs);
    }

    [Theory]
    [InlineData(KodoState.Healthy, true)]
    [InlineData(KodoState.Degraded, true)]
    [InlineData(KodoState.Failing, false)]
    [InlineData(KodoState.Recovering, false)]
    [InlineData(KodoState.Frozen, false)]
    public async Task ChecksRunOnlyInHealthyAndDegraded(KodoState state, bool runs)
    {
        StubCheck check = new("selfcheck.checksums", ProbeResult.Green);
        SelfCheckRunner runner = new([new ScheduledSelfCheck(check, SelfCheckRunner.Daily)], Clock());

        IReadOnlyList<SelfCheckRun> result = await runner.RunDueAsync(state, _ct);

        Assert.Equal(runs, result.Count == 1);
        Assert.Equal(runs ? 1 : 0, check.Runs);
        Assert.Equal(runs ? 0 : 1, runner.Due().Count);
    }

    [Fact]
    public async Task AThrowingCheckIsARedProbeExceptionRunNotASilentSkip()
    {
        FakeTimeProvider time = Clock();
        ThrowingCheck check = new();
        SelfCheckRunner runner = new([new ScheduledSelfCheck(check, SelfCheckRunner.Daily)], time);

        SelfCheckRun run = Assert.Single(await runner.RunDueAsync(KodoState.Healthy, _ct));

        Assert.Equal(ProbeStatus.Red, run.Result.Status);
        Assert.Equal(ProbeCauses.ProbeException, run.Result.Cause);
        Assert.IsType<InvalidOperationException>(run.Result.Error);
        Assert.Equal(time.GetUtcNow(), run.At);
    }

    private sealed class ThrowingCheck : ISelfCheck
    {
        public string Id => "selfcheck.key_recovery";

        public Task<ProbeResult> RunAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task CancellationStopsTheRunAndIsNotRecordedAsAFailure()
    {
        SelfCheckRunner runner = new([new ScheduledSelfCheck(new CancellingCheck(), SelfCheckRunner.Daily)], Clock());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunDueAsync(KodoState.Healthy, cts.Token));
        Assert.Single(runner.Due());
    }

    private sealed class CancellingCheck : ISelfCheck
    {
        public string Id => "selfcheck.restore";

        public Task<ProbeResult> RunAsync(CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
    }

    [Fact]
    public void BadConfigurationIsRejected()
    {
        StubCheck a = new("selfcheck.checksums", ProbeResult.Green);
        StubCheck dup = new("selfcheck.checksums", ProbeResult.Green);

        Assert.Throws<ArgumentException>(() => new SelfCheckRunner([new ScheduledSelfCheck(a, SelfCheckRunner.Daily), new ScheduledSelfCheck(dup, SelfCheckRunner.Daily)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SelfCheckRunner([new ScheduledSelfCheck(a, TimeSpan.Zero)]));
    }

    [Fact]
    public void ObservationMappingTreatsSelfChecksAsNonCritical()
    {
        DateTimeOffset at = DateTimeOffset.UnixEpoch;

        KodoObservation green = SelfCheckRunner.ToObservation([new SelfCheckRun("a", at, ProbeResult.Green())]);
        KodoObservation red = SelfCheckRunner.ToObservation([new SelfCheckRun("a", at, ProbeResult.Red("x")), new SelfCheckRun("b", at, ProbeResult.Red("y"))]);
        KodoObservation unknown = SelfCheckRunner.ToObservation([new SelfCheckRun("a", at, ProbeResult.Unknown("x"))]);
        KodoObservation amber = SelfCheckRunner.ToObservation([new SelfCheckRun("a", at, ProbeResult.Amber("x"))]);

        Assert.True(green.AllRulesGreen);
        Assert.Equal(2, red.NonCriticalRedBeyondGrace);
        Assert.False(red.CriticalRed);
        Assert.True(unknown.NonCriticalAmberBeyondGrace);
        Assert.True(amber.NonCriticalAmberBeyondGrace);
        Assert.False(unknown.AllRulesGreen);
    }

    [Fact]
    public async Task AnIntegrityFindingDegradesTheEvaluatorButDoesNotFailIt()
    {
        FakeFileSource source = new();
        source.Files["/data/a"] = [1];
        ChecksumManifest manifest = new([KeyValuePair.Create("/data/a", ChecksumManifest.Hash([2]))]);
        SelfCheckRunner runner = new(
            [new ScheduledSelfCheck(new ChecksumSelfCheck(["/data"], new FakeManifestStore(manifest), source), SelfCheckRunner.Daily)],
            Clock());

        IReadOnlyList<SelfCheckRun> runs = await runner.RunDueAsync(KodoState.Healthy, _ct);
        KodoEvaluation evaluation = KodoEvaluator.Evaluate(
            KodoEvaluator.Initial, SelfCheckRunner.ToObservation(runs), DateTimeOffset.UnixEpoch, new KodoEvaluatorSettings());

        Assert.Equal(KodoState.Degraded, evaluation.State);
    }
}
