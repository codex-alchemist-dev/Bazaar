using Bazaar.Kodo.Testing;

namespace Bazaar.Kodo.Tests.Testing;

/// <summary>The resilience score (doc 46 section 17.3).</summary>
public sealed class ChaosResilienceScoreTests
{
    private static readonly TimeSpan _target = TimeSpan.FromMinutes(1);

    private static ChaosExperimentResult Result(
        bool held = true,
        TimeSpan? recovery = null,
        bool dataOk = true,
        bool alert = true,
        bool unexpected = false,
        bool? runbook = null,
        ChaosOutcome outcome = ChaosOutcome.Completed,
        int sequence = 1) =>
        new(new ChaosExperiment(sequence, ChaosFaultKind.KillModule, 10, TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch),
            outcome, held, recovery ?? TimeSpan.FromSeconds(30), dataOk, alert, unexpected, runbook);

    [Fact]
    public void NoExperimentsGiveNoScore()
    {
        ChaosScoreReport report = ChaosResilienceScore.Compute([], _target);

        Assert.Null(report.Score);
    }

    [Fact]
    public void AllGoodIsOneHundred()
    {
        ChaosScoreReport report = ChaosResilienceScore.Compute([Result(runbook: true), Result(sequence: 2)], _target);

        Assert.Equal(100, report.Score);
        Assert.False(report.Capped);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ASingleDataLossFailureCapsAtFortyAndIsAFinding()
    {
        ChaosScoreReport report = ChaosResilienceScore.Compute(
            [Result(), Result(sequence: 2), Result(sequence: 3), Result(dataOk: false, sequence: 4)], _target);

        Assert.Equal(40, report.Score);
        Assert.True(report.Capped);
        Assert.Contains("experiment 4", Assert.Single(report.Findings), StringComparison.Ordinal);
    }

    [Fact]
    public void ALowScoreWithDataLossIsNotRaisedToTheCap()
    {
        ChaosExperimentResult bad = Result(held: false, recovery: TimeSpan.FromHours(10), dataOk: false, alert: false);

        ChaosScoreReport report = ChaosResilienceScore.Compute([bad], _target);

        Assert.True(report.Score < 40);
        Assert.False(report.Capped);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void SlowRecoveryLowersTheScoreInProportionToTheTarget()
    {
        ChaosScoreReport slow = ChaosResilienceScore.Compute([Result(recovery: TimeSpan.FromMinutes(2))], _target);

        Assert.Equal(0.5, slow.RecoveryScore, 6);
        Assert.Equal(88, slow.Score);
    }

    [Fact]
    public void ARunThatNeverRecoveredScoresZeroForRecovery()
    {
        ChaosExperimentResult never = Result() with { Recovery = null };

        ChaosScoreReport report = ChaosResilienceScore.Compute([never], _target);

        Assert.Equal(0, report.RecoveryScore);
        Assert.Equal(75, report.Score);
    }

    [Fact]
    public void ARecoveryExactlyOnTargetIsFullMarks()
    {
        ChaosScoreReport report = ChaosResilienceScore.Compute([Result(recovery: _target)], _target);

        Assert.Equal(1, report.RecoveryScore);
    }

    [Fact]
    public void AMissingAlertOrAnUnexpectedCriticalAlertBothCount()
    {
        ChaosScoreReport report = ChaosResilienceScore.Compute(
            [Result(), Result(alert: false, sequence: 2), Result(unexpected: true, sequence: 3), Result(sequence: 4)], _target);

        Assert.Equal(0.5, report.AlertCorrectness, 6);
    }

    [Fact]
    public void RunbookSuccessCountsOnlyExperimentsThatNeededOne()
    {
        ChaosScoreReport none = ChaosResilienceScore.Compute([Result()], _target);
        ChaosScoreReport mixed = ChaosResilienceScore.Compute([Result(runbook: true), Result(runbook: false, sequence: 2), Result(sequence: 3)], _target);

        Assert.Equal(1, none.RunbookSuccess);
        Assert.Equal(0.5, mixed.RunbookSuccess, 6);
    }

    [Fact]
    public void ABadTargetIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChaosResilienceScore.Compute([Result()], TimeSpan.Zero));
    }
}
