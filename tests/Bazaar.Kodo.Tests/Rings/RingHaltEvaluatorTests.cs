using Bazaar.Kodo.Rings;

namespace Bazaar.Kodo.Tests.Rings;

public sealed class RingHaltEvaluatorTests
{
    private static readonly RingHaltEvaluator _evaluator = new(new RingHaltOptions());

    private static RingEvaluation Run(CohortSignals cohort, CohortSignals? baseline = null) =>
        _evaluator.Evaluate(cohort, baseline ?? RingTestKit.Baseline());

    [Fact]
    public void AHealthyCohortIsHealthy()
    {
        RingEvaluation result = Run(RingTestKit.Cohort());

        Assert.Equal(RingVerdict.Healthy, result.Verdict);
        Assert.Empty(result.Reasons);
        Assert.Empty(result.InsufficientCodes);
    }

    [Fact]
    public void CrashRateAtTwiceBaselineWithTwentyEventsHalts()
    {
        // Baseline 4 per 2000 sessions is 2 per 1,000; 2x is 4 per 1,000, which is 8 per 2000 and the 20 event minimum.
        RingEvaluation result = Run(RingTestKit.Cohort() with { Crashes = 20, Sessions = 2000 }, RingTestKit.Baseline() with { Crashes = 4 });

        HaltReason reason = Assert.Single(result.Reasons);
        Assert.Equal(RingRuleIds.CrashRate, reason.RuleId);
        Assert.Equal(10m, reason.Observed);
        Assert.Equal(4m, reason.Threshold);
        Assert.Equal(RingVerdict.Halt, result.Verdict);
    }

    [Fact]
    public void CrashRateJustUnderTwiceBaselineDoesNotHalt()
    {
        CohortSignals baseline = RingTestKit.Baseline() with { Crashes = 20 };

        // Baseline is 10 per 1,000, so the threshold is 20 per 1,000 (40 crashes in 2000 sessions).
        Assert.Equal(RingVerdict.Healthy, Run(RingTestKit.Cohort() with { Crashes = 39 }, baseline).Verdict);
        Assert.Equal(RingVerdict.Halt, Run(RingTestKit.Cohort() with { Crashes = 40 }, baseline).Verdict);
    }

    [Fact]
    public void CrashRateNeedsAtLeastTwentyEvents()
    {
        // 19 crashes in 2000 sessions is 9.5 per 1,000, far over 2x a 1 per 1,000 floor, but under the event minimum.
        RingEvaluation result = Run(RingTestKit.Cohort() with { Crashes = 19 }, RingTestKit.Baseline() with { Crashes = 0 });

        Assert.Equal(RingVerdict.Healthy, result.Verdict);
        Assert.Equal(RingVerdict.Halt, Run(RingTestKit.Cohort() with { Crashes = 20 }, RingTestKit.Baseline() with { Crashes = 0 }).Verdict);
    }

    [Fact]
    public void ASpotlessBaselineDoesNotMakeEveryCrashAHalt()
    {
        // 20 crashes in 50,000 sessions is 0.4 per 1,000, under the 2 per 1,000 (floor 1 times 2) threshold.
        CohortSignals cohort = RingTestKit.Cohort() with { Sessions = 50_000, Crashes = 20, StreamAttempts = 50_000 };
        CohortSignals baseline = RingTestKit.Baseline() with { Crashes = 0 };

        Assert.Equal(RingVerdict.Healthy, Run(cohort, baseline).Verdict);
    }

    [Fact]
    public void ANewCriticalContractFailureHaltsEvenWithoutData()
    {
        CohortSignals cohort = RingTestKit.Cohort() with
        {
            Devices = 1,
            Sessions = 3,
            ContractFailures = [new ContractFailure("gc_core", "ready_timeout", Critical: true)],
        };

        RingEvaluation result = _evaluator.Evaluate(cohort, null);

        Assert.Equal(RingVerdict.Halt, result.Verdict);
        Assert.Equal(RingRuleIds.ModuleContract, Assert.Single(result.Reasons).RuleId);
        Assert.Equal("gc_core", Assert.Single(result.NewCriticalModules));
        Assert.Contains(RingInsufficientCodes.BaselineMissing, result.InsufficientCodes);
    }

    [Fact]
    public void AContractFailureAlreadyInTheBaselineIsNotNewAndANonCriticalOneNeverHalts()
    {
        ContractFailure known = new("gc_core", "ready_timeout", true);
        CohortSignals baseline = RingTestKit.Baseline() with { ContractFailures = [known] };

        Assert.Equal(RingVerdict.Healthy, Run(RingTestKit.Cohort() with { ContractFailures = [known] }, baseline).Verdict);
        Assert.Equal(
            RingVerdict.Healthy,
            Run(RingTestKit.Cohort() with { ContractFailures = [new ContractFailure("notes", "slow", false)] }).Verdict);
        Assert.Equal(
            RingVerdict.Halt,
            Run(RingTestKit.Cohort() with { ContractFailures = [known with { Code = "other_code" }] }, baseline).Verdict);
    }

    [Fact]
    public void ACriticalProbeRedOnTheNewVersionHalts()
    {
        RingEvaluation result = Run(RingTestKit.Cohort() with { RedCriticalProbes = ["db.ready"] });

        Assert.Equal(RingVerdict.Halt, result.Verdict);
        Assert.Equal(RingRuleIds.CriticalProbeRed, Assert.Single(result.Reasons).RuleId);
    }

    [Fact]
    public void AFailedSelfTestAndAFastSloBurnHalt()
    {
        Assert.Equal(RingRuleIds.SelfTest, Assert.Single(Run(RingTestKit.Cohort() with { SelfTestsFailed = 1 }).Reasons).RuleId);
        Assert.Equal(RingRuleIds.SloBurn, Assert.Single(Run(RingTestKit.Cohort() with { SloBurnRate = 6m }).Reasons).RuleId);
        Assert.Equal(RingVerdict.Healthy, Run(RingTestKit.Cohort() with { SloBurnRate = 5.9m }).Verdict);
    }

    [Fact]
    public void ErrorCodeAndStreamFailureRatesHaltAgainstTheBaseline()
    {
        RingEvaluation errors = Run(RingTestKit.Cohort() with { ErrorEvents = 200 });
        RingEvaluation streams = Run(RingTestKit.Cohort() with { StreamFailures = 100 });

        Assert.Equal(RingRuleIds.ErrorCodeRate, Assert.Single(errors.Reasons).RuleId);
        Assert.Equal(RingRuleIds.StreamFailureRate, Assert.Single(streams.Reasons).RuleId);
    }

    [Theory]
    [InlineData(1, 2000, 60, RingInsufficientCodes.TooFewDevices)]
    [InlineData(10, 100, 60, RingInsufficientCodes.TooFewSessions)]
    [InlineData(10, 2000, 30, RingInsufficientCodes.WindowShort)]
    public void ThinOrShortDataIsInsufficientNeverHealthy(int devices, long sessions, int windowMinutes, string code)
    {
        CohortSignals cohort = RingTestKit.Cohort() with
        {
            Devices = devices,
            Sessions = sessions,
            StreamAttempts = sessions,
            Crashes = 0,
            WindowEnd = RingTestKit.Start.AddMinutes(windowMinutes),
        };

        RingEvaluation result = Run(cohort);

        Assert.Equal(RingVerdict.InsufficientData, result.Verdict);
        Assert.Contains(code, result.InsufficientCodes);
    }

    [Fact]
    public void ASilentCohortIsNotHealthy()
    {
        CohortSignals silent = RingTestKit.Cohort() with { Devices = 0, Sessions = 0, Crashes = 0, ErrorEvents = 0, StreamAttempts = 0, StreamFailures = 0 };

        Assert.Equal(RingVerdict.InsufficientData, Run(silent).Verdict);
    }

    [Fact]
    public void AMissingTooSmallOrSameReleaseBaselineIsInsufficient()
    {
        CohortSignals cohort = RingTestKit.Cohort();

        Assert.Contains(RingInsufficientCodes.BaselineMissing, _evaluator.Evaluate(cohort, null).InsufficientCodes);
        Assert.Contains(RingInsufficientCodes.BaselineMissing, _evaluator.Evaluate(cohort, RingTestKit.Baseline("v0.2.0")).InsufficientCodes);
        Assert.Contains(
            RingInsufficientCodes.BaselineTooSmall,
            _evaluator.Evaluate(cohort, RingTestKit.Baseline() with { Sessions = 10 }).InsufficientCodes);
    }

    [Fact]
    public void MalformedSignalsAreInsufficient()
    {
        Assert.Equal(RingVerdict.InsufficientData, Run(RingTestKit.Cohort() with { Crashes = -1 }).Verdict);
        Assert.Equal(RingVerdict.InsufficientData, Run(RingTestKit.Cohort() with { StreamFailures = 5000 }).Verdict);
        Assert.Equal(RingVerdict.InsufficientData, Run(RingTestKit.Cohort() with { WindowEnd = RingTestKit.Start.AddMinutes(-1) }).Verdict);
        Assert.Equal(RingVerdict.InsufficientData, Run(RingTestKit.Cohort() with { RedCriticalProbes = null! }).Verdict);
    }

    [Fact]
    public void ATrippedRuleHaltsEvenWhenOtherRulesLackData()
    {
        RingEvaluation result = Run(RingTestKit.Cohort() with { Devices = 1, SelfTestsFailed = 2 });

        Assert.Equal(RingVerdict.Halt, result.Verdict);
        Assert.Contains(RingInsufficientCodes.TooFewDevices, result.InsufficientCodes);
    }

    [Fact]
    public void SettingsMayTightenButNeverLoosenTheFloors()
    {
        RingHaltOptions tighter = new()
        {
            CrashMultiple = 1.5m,
            CrashMinEvents = 10,
            ObservationWindow = TimeSpan.FromMinutes(30),
            MinDevices = 10,
            MinSessions = 1000,
        };
        tighter.Validate();

        // 12 crashes is 6 per 1,000: over 1.5x a 2 per 1,000 baseline, under 2x of it with the default 20 events.
        CohortSignals cohort = RingTestKit.Cohort() with { Crashes = 12, WindowEnd = RingTestKit.Start.AddMinutes(30) };
        Assert.Equal(RingVerdict.Halt, new RingHaltEvaluator(tighter).Evaluate(cohort, RingTestKit.Baseline()).Verdict);
        Assert.Equal(RingVerdict.InsufficientData, _evaluator.Evaluate(cohort, RingTestKit.Baseline()).Verdict);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { CrashMultiple = 3m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { CrashMinEvents = 21 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { CrashMinEvents = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { ObservationWindow = TimeSpan.FromHours(2) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { ObservationWindow = TimeSpan.FromMinutes(5) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { MinDevices = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { MinSessions = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { SloBurnHalt = 10m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingHaltEvaluator(new RingHaltOptions { CrashFloorPer1000 = 5m }));
    }
}
