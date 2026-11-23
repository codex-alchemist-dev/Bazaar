using Bazaar.Kodo.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Testing;

internal sealed class FakeGate(bool authorized = true, bool incident = false) : IChaosGate
{
    public bool Authorized { get; set; } = authorized;

    public bool Incident { get; set; } = incident;

    public ChaosGateState Current => new(Authorized, Incident);
}

internal sealed class FakeSandbox : IChaosSandbox
{
    public bool IsSandbox { get; set; } = true;

    public bool HonoursCancel { get; set; } = true;

    public bool Throws { get; set; }

    public List<ChaosExperiment> Ran { get; } = [];

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool CompleteImmediately { get; set; } = true;

    public async Task<ChaosExperimentResult> RunAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        Ran.Add(experiment);
        Started.TrySetResult();
        if (Throws)
        {
            throw new InvalidOperationException("sandbox failed");
        }

        if (!CompleteImmediately)
        {
            if (HonoursCancel)
            {
                await Finish.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Finish.Task.ConfigureAwait(false);
            }
        }

        return new ChaosExperimentResult(experiment, ChaosOutcome.Completed, true, TimeSpan.FromSeconds(30), true, true, false, null);
    }
}

/// <summary>The chaos scheduler (doc 46 section 17.3): off by default, allow-listed, sandbox only, seeded, abortable.</summary>
public sealed class ChaosSchedulerTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly IReadOnlySet<ChaosFaultKind> _two = new HashSet<ChaosFaultKind> { ChaosFaultKind.KillModule, ChaosFaultKind.SkewClock };

    private static ChaosOptions Options(int seed = 7) => new() { Enabled = true, AllowedKinds = _two, Seed = seed };

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));

    private static ChaosScheduler Make(ChaosOptions options, FakeGate? gate = null, FakeSandbox? sandbox = null, FakeTimeProvider? time = null) =>
        new(options, gate ?? new FakeGate(), sandbox ?? new FakeSandbox(), time ?? Clock());

    [Fact]
    public void DefaultOptionsNeverRunAnything()
    {
        ChaosOptions defaults = new();
        FakeTimeProvider time = Clock();
        ChaosScheduler scheduler = Make(defaults with { AllowedKinds = _two }, time: time);

        time.Advance(TimeSpan.FromDays(30));

        Assert.False(defaults.Enabled);
        Assert.Empty(defaults.AllowedKinds);
        Assert.Equal(ChaosDecisionKind.Disabled, scheduler.Next().Kind);
    }

    [Fact]
    public async Task DisabledSchedulerNeverCallsTheSandbox()
    {
        FakeSandbox sandbox = new();
        FakeTimeProvider time = Clock();
        ChaosScheduler scheduler = Make(Options() with { Enabled = false }, sandbox: sandbox, time: time);
        time.Advance(TimeSpan.FromDays(30));

        Assert.Null(await scheduler.RunDueAsync(_ct));
        Assert.Empty(sandbox.Ran);
    }

    [Fact]
    public void GateStatesBlockBeforeAnythingIsScheduled()
    {
        Assert.Equal(ChaosDecisionKind.GateClosed, Make(Options(), gate: new FakeGate(authorized: false)).Next().Kind);
        Assert.Equal(ChaosDecisionKind.IncidentOpen, Make(Options(), gate: new FakeGate(incident: true)).Next().Kind);
        Assert.Equal(ChaosDecisionKind.NotSandbox, Make(Options(), sandbox: new FakeSandbox { IsSandbox = false }).Next().Kind);
        Assert.Equal(ChaosDecisionKind.NoAllowedKinds, Make(Options() with { AllowedKinds = new HashSet<ChaosFaultKind>() }).Next().Kind);
    }

    [Fact]
    public void AnIncidentBlocksEvenWhenAuthorisedAndDue()
    {
        FakeGate gate = new();
        FakeTimeProvider time = Clock();
        ChaosScheduler scheduler = Make(Options(), gate, time: time);
        Assert.Equal(ChaosDecisionKind.NotDue, scheduler.Next().Kind);
        time.Advance(TimeSpan.FromDays(1));

        gate.Incident = true;

        Assert.Equal(ChaosDecisionKind.IncidentOpen, scheduler.Next().Kind);
    }

    [Fact]
    public void FirstCallSchedulesWithinTheIntervalThenStartsOnceDue()
    {
        ChaosOptions options = Options();
        FakeTimeProvider time = Clock();
        DateTimeOffset start = time.GetUtcNow();
        ChaosScheduler scheduler = Make(options, time: time);

        ChaosDecision first = scheduler.Next();

        Assert.Equal(ChaosDecisionKind.NotDue, first.Kind);
        Assert.InRange(first.NextDueAt!.Value - start, options.MinInterval, options.MaxInterval);

        time.Advance(options.MaxInterval);
        ChaosDecision start1 = scheduler.Next();

        Assert.Equal(ChaosDecisionKind.Start, start1.Kind);
        Assert.Contains(start1.Experiment!.Kind, _two);
        Assert.InRange(start1.Experiment.IntensityPercent, 1, options.MaxIntensityPercent);
        Assert.Equal(options.MaxExperimentDuration, start1.Experiment.MaxDuration);
        Assert.Equal(time.GetUtcNow(), start1.Experiment.StartedAt);
        Assert.Equal(1, start1.Experiment.Sequence);
        Assert.InRange(start1.NextDueAt!.Value - time.GetUtcNow(), options.MinInterval, options.MaxInterval);
    }

    [Fact]
    public void AStartedExperimentMakesTheSchedulerBusyUntilItFinishes()
    {
        FakeTimeProvider time = Clock();
        ChaosScheduler scheduler = Make(Options(), time: time);
        scheduler.Next();
        time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(ChaosDecisionKind.Start, scheduler.Next().Kind);
        Assert.Equal(ChaosDecisionKind.Busy, scheduler.Next().Kind);
    }

    [Fact]
    public void ASeedGivesTheSameSequenceAndAnotherSeedDoesNot()
    {
        static List<(ChaosFaultKind, int, DateTimeOffset)> Draw(int seed)
        {
            FakeTimeProvider time = Clock();
            ChaosOptions options = Options(seed) with { AllowedKinds = Enum.GetValues<ChaosFaultKind>().ToHashSet() };
            // Fresh scheduler per draw: Start marks it busy, and this test only reads decisions.
            List<(ChaosFaultKind, int, DateTimeOffset)> draws = [];
            ChaosScheduler scheduler = Make(options, time: time);
            scheduler.Next();
            for (int i = 0; i < 12; i++)
            {
                time.Advance(TimeSpan.FromDays(1));
                ChaosDecision decision = scheduler.Next();
                draws.Add((decision.Experiment!.Kind, decision.Experiment.IntensityPercent, decision.NextDueAt!.Value));
                scheduler = Make(options with { Seed = seed + i + 1 }, time: time);
                scheduler.Next();
            }

            return draws;
        }

        Assert.Equal(Draw(5), Draw(5));
        Assert.NotEqual(Draw(5), Draw(6));
    }

    [Fact]
    public async Task OnlyAllowListedKindsAreEverChosenAndAllOfThemAppear()
    {
        FakeTimeProvider time = Clock();
        FakeSandbox sandbox = new();
        ChaosScheduler scheduler = Make(Options(3), sandbox: sandbox, time: time);
        await scheduler.RunDueAsync(_ct);

        for (int i = 0; i < 60; i++)
        {
            time.Advance(TimeSpan.FromHours(7));
            await scheduler.RunDueAsync(_ct);
        }

        Assert.Equal(60, sandbox.Ran.Count);
        Assert.All(sandbox.Ran, e => Assert.Contains(e.Kind, _two));
        Assert.Equal(_two.OrderBy(k => k), sandbox.Ran.Select(e => e.Kind).Distinct().OrderBy(k => k));
        Assert.Equal(Enumerable.Range(1, 60), sandbox.Ran.Select(e => e.Sequence));
    }

    [Fact]
    public async Task ACompletedRunReturnsTheSandboxResultAndFreesTheScheduler()
    {
        FakeTimeProvider time = Clock();
        FakeSandbox sandbox = new();
        ChaosScheduler scheduler = Make(Options(), sandbox: sandbox, time: time);
        await scheduler.RunDueAsync(_ct);
        time.Advance(TimeSpan.FromDays(1));

        ChaosExperimentResult? result = await scheduler.RunDueAsync(_ct);

        Assert.NotNull(result);
        Assert.Equal(ChaosOutcome.Completed, result.Outcome);
        Assert.Equal(ChaosDecisionKind.NotDue, scheduler.Next().Kind);
    }

    [Fact]
    public async Task ASandboxThatThrowsIsAFailedRunAndFreesTheScheduler()
    {
        FakeTimeProvider time = Clock();
        FakeSandbox sandbox = new() { Throws = true };
        ChaosScheduler scheduler = Make(Options(), sandbox: sandbox, time: time);
        await scheduler.RunDueAsync(_ct);
        time.Advance(TimeSpan.FromDays(1));

        ChaosExperimentResult? result = await scheduler.RunDueAsync(_ct);

        Assert.Equal(ChaosOutcome.Failed, result!.Outcome);
        Assert.False(result.HypothesisHeld);
        Assert.NotEqual(ChaosDecisionKind.Busy, scheduler.Next().Kind);
    }

    /// <summary>Starts a long experiment and returns the task and the time spent advancing the fake clock to settle it.</summary>
    private static async Task<(ChaosExperimentResult? Result, TimeSpan Advanced)> RunAndAbort(
        ChaosOptions options, FakeGate gate, FakeSandbox sandbox, FakeTimeProvider time, Action abort)
    {
        sandbox.CompleteImmediately = false;
        ChaosScheduler scheduler = Make(options, gate, sandbox, time);
        await scheduler.RunDueAsync(_ct);
        time.Advance(TimeSpan.FromDays(1));

        Task<ChaosExperimentResult?> run = Task.Run(() => scheduler.RunDueAsync(_ct), _ct);
        await sandbox.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        abort();

        TimeSpan advanced = TimeSpan.Zero;
        for (int i = 0; i < 40 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            advanced += TimeSpan.FromSeconds(1);
            await Task.Delay(5, _ct);
        }

        return (await run.WaitAsync(TimeSpan.FromSeconds(10), _ct), advanced);
    }

    [Fact]
    public async Task AnIncidentOpeningMidRunAbortsWithinTheDeadline()
    {
        FakeGate gate = new();
        FakeSandbox sandbox = new();

        (ChaosExperimentResult? result, TimeSpan advanced) = await RunAndAbort(Options(), gate, sandbox, Clock(), () => gate.Incident = true);

        Assert.Equal(ChaosOutcome.Aborted, result!.Outcome);
        Assert.False(result.HypothesisHeld);
        Assert.True(advanced <= ChaosOptions.AbortDeadlineCeiling, $"Abort took {advanced}.");
    }

    [Fact]
    public async Task RevokingAuthorisationMidRunAborts()
    {
        FakeGate gate = new();

        (ChaosExperimentResult? result, _) = await RunAndAbort(Options(), gate, new FakeSandbox(), Clock(), () => gate.Authorized = false);

        Assert.Equal(ChaosOutcome.Aborted, result!.Outcome);
    }

    [Fact]
    public async Task ExceedingTheDurationLimitAborts()
    {
        ChaosOptions options = Options() with { MaxExperimentDuration = TimeSpan.FromSeconds(3) };

        (ChaosExperimentResult? result, TimeSpan advanced) = await RunAndAbort(options, new FakeGate(), new FakeSandbox(), Clock(), () => { });

        Assert.Equal(ChaosOutcome.Aborted, result!.Outcome);
        Assert.True(advanced <= TimeSpan.FromSeconds(3) + ChaosOptions.AbortDeadlineCeiling);
    }

    [Fact]
    public async Task ASandboxThatIgnoresTheAbortIsAnAbortTimeoutFinding()
    {
        FakeGate gate = new();
        FakeSandbox sandbox = new() { HonoursCancel = false };

        (ChaosExperimentResult? result, TimeSpan advanced) = await RunAndAbort(Options(), gate, sandbox, Clock(), () => gate.Incident = true);
        sandbox.Finish.TrySetResult();

        Assert.Equal(ChaosOutcome.AbortTimedOut, result!.Outcome);
        Assert.True(advanced <= ChaosOptions.AbortDeadlineCeiling, $"Gave up after {advanced}.");
        Assert.Contains("did not stop", ChaosResilienceScore.Compute([result], TimeSpan.FromMinutes(1)).Findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsEnforceTheirLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { AbortDeadline = TimeSpan.FromSeconds(11) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { AbortDeadline = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { WatchInterval = TimeSpan.FromSeconds(10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { MaxIntensityPercent = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { MaxIntensityPercent = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { MinInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { MaxInterval = TimeSpan.FromMinutes(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Options() with { MaxExperimentDuration = TimeSpan.Zero }));
    }
}
