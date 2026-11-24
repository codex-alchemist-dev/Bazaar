using System.Text.RegularExpressions;
using Bazaar.Kodo.Responder;
using Bazaar.Kodo.Rings;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Rings;

public sealed class RingRolloutTests
{
    private static readonly RingChannelProfile _stable = RingChannelProfile.Defaults[ReleaseChannel.Stable];

    private static ReleaseRingMonitor Monitor(FakeTimeProvider? clock = null) =>
        new(null, clock ?? new FakeTimeProvider(RingTestKit.Start.AddHours(1)));

    private static RingCheck HaltingCheck(out ReleaseRollout live)
    {
        live = RingTestKit.LiveCanary();
        return Monitor().Check(live, ReleaseRing.Canary, RingTestKit.Cohort() with { RedCriticalProbes = ["db.ready"] }, RingTestKit.Baseline());
    }

    [Fact]
    public void RingsStartInOrderAndOnlyAfterTheEarlierOneClears()
    {
        ReleaseRollout rollout = ReleaseRollout.Begin("v0.2.0", ReleaseChannel.Stable);

        Assert.Equal("earlier_ring_not_cleared", RingRollouts.Start(rollout, ReleaseRing.Early, _stable, RingTestKit.Start).Refusal);
        Assert.Equal("earlier_ring_not_cleared", RingRollouts.Start(rollout, ReleaseRing.Broad, _stable, RingTestKit.Start).Refusal);

        ReleaseRollout live = RingRollouts.Start(rollout, ReleaseRing.Canary, _stable, RingTestKit.Start).Rollout;
        Assert.Equal("ring_already_started", RingRollouts.Start(live, ReleaseRing.Canary, _stable, RingTestKit.Start).Refusal);
        Assert.Equal("earlier_ring_not_cleared", RingRollouts.Start(live, ReleaseRing.Early, _stable, RingTestKit.Start).Refusal);

        ReleaseRollout cleared = RingRollouts.Clear(live, ReleaseRing.Canary, RingVerdict.Healthy, _stable, RingTestKit.Start.AddHours(25)).Rollout;
        Assert.Equal(RingStatus.Live, RingRollouts.Start(cleared, ReleaseRing.Early, _stable, RingTestKit.Start.AddHours(25)).Rollout.Of(ReleaseRing.Early).Status);
    }

    [Fact]
    public void ClearingNeedsProbationAndAHealthyVerdict()
    {
        ReleaseRollout live = RingTestKit.LiveCanary();

        Assert.Equal("probation_not_over", RingRollouts.Clear(live, ReleaseRing.Canary, RingVerdict.Healthy, _stable, RingTestKit.Start.AddHours(23)).Refusal);
        Assert.Equal("verdict_not_healthy", RingRollouts.Clear(live, ReleaseRing.Canary, RingVerdict.InsufficientData, _stable, RingTestKit.Start.AddDays(9)).Refusal);
        Assert.Equal("verdict_not_healthy", RingRollouts.Clear(live, ReleaseRing.Canary, RingVerdict.Halt, _stable, RingTestKit.Start.AddDays(9)).Refusal);
        Assert.Equal("ring_not_live", RingRollouts.Clear(live, ReleaseRing.Early, RingVerdict.Healthy, _stable, RingTestKit.Start.AddDays(9)).Refusal);
    }

    [Fact]
    public void LtsNeverSkipsCanaryAndAProfileCannotAllowIt()
    {
        RingChannelProfile lts = RingChannelProfile.Defaults[ReleaseChannel.Lts];
        ReleaseRollout rollout = ReleaseRollout.Begin("v0.2.0", ReleaseChannel.Lts);

        Assert.Equal("earlier_ring_not_cleared", RingRollouts.Start(rollout, ReleaseRing.Early, lts, RingTestKit.Start).Refusal);
        Assert.Throws<ArgumentException>((lts with { AllowSkipCanary = true }).Validate);
        Assert.Throws<ArgumentException>(() => new ReleaseRingMonitor(
            new Dictionary<ReleaseChannel, RingChannelProfile> { [ReleaseChannel.Lts] = lts with { AllowSkipCanary = true } },
            TimeProvider.System));

        RingChannelProfile skipping = _stable with { AllowSkipCanary = true };
        ReleaseRollout stable = ReleaseRollout.Begin("v0.2.0", ReleaseChannel.Stable);
        Assert.Equal(RingStatus.Live, RingRollouts.Start(stable, ReleaseRing.Early, skipping, RingTestKit.Start).Rollout.Of(ReleaseRing.Early).Status);
    }

    [Fact]
    public void NightlyAndBetaAreMoreSensitiveWithShorterProbationThanStable()
    {
        RingChannelProfile stable = RingChannelProfile.Defaults[ReleaseChannel.Stable];
        foreach (ReleaseChannel channel in new[] { ReleaseChannel.Beta, ReleaseChannel.Nightly })
        {
            RingChannelProfile profile = RingChannelProfile.Defaults[channel];
            Assert.True(profile.Halt.CrashMultiple < stable.Halt.CrashMultiple);
            Assert.True(profile.Halt.CrashMinEvents < stable.Halt.CrashMinEvents);
            Assert.True(profile.Halt.SloBurnHalt < stable.Halt.SloBurnHalt);
            Assert.True(profile.Probation < stable.Probation);
        }

        Assert.True(RingChannelProfile.Defaults[ReleaseChannel.Lts].Probation > stable.Probation);
        Assert.All(RingChannelProfile.Defaults.Values, profile => profile.Validate());
    }

    [Fact]
    public void ASensitiveChannelHaltsWhereStableDoesNot()
    {
        // 12 crashes in 2000 sessions: 6 per 1,000 against a baseline of 2. Over 1.5x, under the stable 20 event minimum.
        CohortSignals cohort = RingTestKit.Cohort() with { Crashes = 12 };
        RingEvaluation stable = new RingHaltEvaluator(RingChannelProfile.Defaults[ReleaseChannel.Stable].Halt).Evaluate(cohort, RingTestKit.Baseline());
        RingEvaluation nightly = new RingHaltEvaluator(RingChannelProfile.Defaults[ReleaseChannel.Nightly].Halt).Evaluate(
            cohort with { WindowEnd = cohort.WindowStart.AddMinutes(30) }, RingTestKit.Baseline());

        Assert.Equal(RingVerdict.Healthy, stable.Verdict);
        Assert.Equal(RingVerdict.Halt, nightly.Verdict);
    }

    [Fact]
    public void HaltingChangesOnlyTheHaltedRingAndBlocksLaterRings()
    {
        RingCheck check = HaltingCheck(out ReleaseRollout live);

        Assert.Equal(RingVerdict.Halt, check.Evaluation.Verdict);
        Assert.NotNull(check.Decision);
        Assert.Equal(RingStatus.Halted, check.Rollout.Of(ReleaseRing.Canary).Status);
        Assert.Equal(check.Decision.Digest, check.Rollout.Of(ReleaseRing.Canary).HaltDigest);
        Assert.Equal(live.Of(ReleaseRing.Early), check.Rollout.Of(ReleaseRing.Early));
        Assert.Equal(live.Of(ReleaseRing.Broad), check.Rollout.Of(ReleaseRing.Broad));
        Assert.Equal("rollout_halted", RingRollouts.Start(check.Rollout, ReleaseRing.Early, _stable, RingTestKit.Start).Refusal);
        Assert.Equal(RingHaltDecision.Effect, check.Decision.ToIdentifiers()["effect"]);
        Assert.Equal("rollout_stopped", RingHaltDecision.Effect);
    }

    [Fact]
    public void MonitorNeverCallsAnInsufficientRingHealthyAndRefusesStaleOrWrongInput()
    {
        ReleaseRollout live = RingTestKit.LiveCanary();
        ReleaseRingMonitor monitor = Monitor();
        CohortSignals thin = RingTestKit.Cohort() with { Devices = 1 };

        Assert.Equal(RingVerdict.InsufficientData, monitor.Check(live, ReleaseRing.Canary, thin, RingTestKit.Baseline()).Evaluation.Verdict);
        Assert.Equal(RingStatus.Live, monitor.Check(live, ReleaseRing.Canary, thin, RingTestKit.Baseline()).Rollout.Of(ReleaseRing.Canary).Status);

        RingCheck notLive = monitor.Check(live, ReleaseRing.Early, RingTestKit.Cohort(ring: ReleaseRing.Early), RingTestKit.Baseline());
        Assert.Contains(RingInsufficientCodes.RingNotLive, notLive.Evaluation.InsufficientCodes);

        RingCheck wrongRing = monitor.Check(live, ReleaseRing.Canary, RingTestKit.Cohort(ring: ReleaseRing.Early), RingTestKit.Baseline());
        Assert.Contains(RingInsufficientCodes.InvalidSignals, wrongRing.Evaluation.InsufficientCodes);

        CohortSignals stale = RingTestKit.Cohort(start: RingTestKit.Start.AddHours(-3));
        RingCheck predates = monitor.Check(live, ReleaseRing.Canary, stale, RingTestKit.Baseline());
        Assert.Equal(RingVerdict.InsufficientData, predates.Evaluation.Verdict);
        Assert.Contains(RingInsufficientCodes.WindowPredatesLive, predates.Evaluation.InsufficientCodes);

        Assert.Equal(RingVerdict.Healthy, monitor.Check(live, ReleaseRing.Canary, RingTestKit.Cohort(), RingTestKit.Baseline()).Evaluation.Verdict);
    }

    [Fact]
    public void ADecisionIsIdentifierOnlyDeterministicAndRedacted()
    {
        RingCheck first = HaltingCheck(out _);
        RingCheck second = HaltingCheck(out _);

        Assert.Equal(first.Decision!.Digest, second.Decision!.Digest);
        Assert.Equal(first.Decision.DecisionId, second.Decision.DecisionId);
        Assert.Matches("^rh-[0-9a-f]{24}$", first.Decision.DecisionId);

        foreach (KeyValuePair<string, string> field in first.Decision.ToIdentifiers())
        {
            Assert.Matches("^[a-z][a-z0-9_]{0,63}$", field.Key);
            Assert.Matches("^[A-Za-z0-9_.:-]{1,128}$", field.Value);
        }

        ReleaseRollout live = RingTestKit.LiveCanary();
        CohortSignals dirty = RingTestKit.Cohort() with
        {
            RedCriticalProbes = ["db.ready", "Alice's laptop failed", "agent.0123456789abcdef0123"],
            ContractFailures = [new ContractFailure("gc core!", "x", true)],
        };
        RingHaltDecision decision = Monitor().Check(live, ReleaseRing.Canary, dirty, RingTestKit.Baseline()).Decision!;

        Assert.Equal(["db.ready"], decision.Probes);
        Assert.Empty(decision.Modules);
        Assert.Equal(3, decision.RedactedCount);
        Assert.DoesNotContain("Alice", System.Text.Encoding.UTF8.GetString(decision.CanonicalBytes()));
        Assert.All(decision.ToIdentifiers().Values, value => Assert.DoesNotMatch(new Regex("[ !']"), value));
    }

    [Fact]
    public void ADecisionRedactsAReleaseIdThatIsNotIdentifierShapedAndStillHalts()
    {
        ReleaseRollout live = ReleaseRollout.Begin("release one!", ReleaseChannel.Stable);
        live = RingRollouts.Start(live, ReleaseRing.Canary, _stable, RingTestKit.Start).Rollout;
        CohortSignals cohort = RingTestKit.Cohort("release one!") with { SelfTestsFailed = 1 };

        RingCheck check = Monitor().Check(live, ReleaseRing.Canary, cohort, RingTestKit.Baseline());

        Assert.Equal(RingHaltDecision.RedactedValue, check.Decision!.ReleaseId);
        Assert.Equal(1, check.Decision.RedactedCount);
        Assert.Equal(RingStatus.Halted, check.Rollout.Of(ReleaseRing.Canary).Status);
    }

    [Fact]
    public void ADecisionIsOnlyMadeForAHalt()
    {
        RingEvaluation healthy = new(RingVerdict.Healthy, [], [], []);

        Assert.Throws<ArgumentException>(() => RingHaltDecision.Create(healthy, RingTestKit.Cohort(), ReleaseChannel.Stable, RingTestKit.Start));
    }

    [Fact]
    public async Task ResumeNeedsTheControlStepUpAndAMatchingExplicitApproval()
    {
        RingCheck check = HaltingCheck(out _);
        RingHaltDecision decision = check.Decision!;
        string requestDigest = ApprovalRingResumeGate.RequestDigest(decision);
        HashSet<string> approver = [KodoControls.RunbookApprove];

        async Task<RingResumeResult> Ask(ApprovalResult? answer, HashSet<string>? controls = null)
        {
            ApprovalRingResumeGate gate = new(new FakeGate(answer));
            return await gate.RequestResumeAsync(new RingResumeRequest(decision, "owner-1", controls ?? approver), TestContext.Current.CancellationToken);
        }

        Assert.Equal("missing_control", (await Ask(null, [KodoControls.RunbookRun])).Refusal);
        Assert.Equal("approval_pending", (await Ask(new ApprovalResult(ApprovalDecision.Pending, "a1", requestDigest, false))).Refusal);
        Assert.Equal("approval_denied", (await Ask(new ApprovalResult(ApprovalDecision.Denied, "a1", requestDigest, true))).Refusal);
        Assert.Equal("approval_timed_out", (await Ask(new ApprovalResult(ApprovalDecision.TimedOut, "a1", requestDigest, true))).Refusal);
        Assert.Equal("step_up_missing", (await Ask(new ApprovalResult(ApprovalDecision.Approved, "a1", requestDigest, false))).Refusal);
        Assert.Equal("digest_mismatch", (await Ask(new ApprovalResult(ApprovalDecision.Approved, "a1", "other", true))).Refusal);
        Assert.Equal("gate_error", (await Ask(null)).Refusal);

        RingResumeResult approved = await Ask(new ApprovalResult(ApprovalDecision.Approved, "a1", requestDigest, true));
        Assert.True(approved.Resumed);
        Assert.Equal("a1", approved.ApprovalId);
        Assert.Equal(decision.Digest, approved.DecisionDigest);
    }

    [Fact]
    public async Task TheGateAsksForStepUpOnTheRunbookApproveControlBoundToTheDecision()
    {
        RingHaltDecision decision = HaltingCheck(out _).Decision!;
        FakeGate fake = new(new ApprovalResult(ApprovalDecision.Pending, "a1", ApprovalRingResumeGate.RequestDigest(decision), false));

        await new ApprovalRingResumeGate(fake).RequestResumeAsync(
            new RingResumeRequest(decision, "owner-1", new HashSet<string> { KodoControls.RunbookApprove }), TestContext.Current.CancellationToken);

        ApprovalRequest sent = Assert.Single(fake.Requests);
        Assert.True(sent.RequireStepUp);
        Assert.Equal(KodoControls.RunbookApprove, sent.RequiredControl);
        Assert.Equal(decision.DecisionId, sent.RunId);
        Assert.False(sent.Tainted);
    }

    [Fact]
    public async Task AnApprovedResumeRestartsProbationAndOnlyForTheSameHalt()
    {
        RingCheck check = HaltingCheck(out _);
        RingHaltDecision decision = check.Decision!;
        DateTimeOffset later = RingTestKit.Start.AddHours(5);

        Assert.Equal("ring_not_halted", RingRollouts.Resume(RingTestKit.LiveCanary(), ReleaseRing.Canary, new RingResumeResult(true, null, "a1", decision.Digest), later).Refusal);
        Assert.Equal("resume_not_approved", RingRollouts.Resume(check.Rollout, ReleaseRing.Canary, new RingResumeResult(false, "approval_pending", null, decision.Digest), later).Refusal);
        Assert.Equal("resume_not_approved", RingRollouts.Resume(check.Rollout, ReleaseRing.Canary, new RingResumeResult(true, null, "a1", "other"), later).Refusal);

        ReleaseRollout resumed = RingRollouts.Resume(check.Rollout, ReleaseRing.Canary, new RingResumeResult(true, null, "a1", decision.Digest), later).Rollout;
        Assert.Equal(RingStatus.Live, resumed.Of(ReleaseRing.Canary).Status);
        Assert.Equal(later, resumed.Of(ReleaseRing.Canary).LiveSince);
        Assert.Equal("probation_not_over", RingRollouts.Clear(resumed, ReleaseRing.Canary, RingVerdict.Healthy, _stable, later.AddHours(23)).Refusal);

        // Data from before the resume does not count toward clearing the ring.
        RingCheck old = Monitor().Check(resumed, ReleaseRing.Canary, RingTestKit.Cohort(), RingTestKit.Baseline());
        Assert.Equal(RingVerdict.InsufficientData, old.Evaluation.Verdict);
    }

    private sealed class FakeGate(ApprovalResult? answer) : IApprovalGate
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public ValueTask<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return answer is null ? throw new InvalidOperationException("gate down") : ValueTask.FromResult(answer);
        }
    }
}
