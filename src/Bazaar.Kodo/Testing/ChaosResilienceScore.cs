namespace Bazaar.Kodo.Testing;

/// <summary>The resilience report (doc 46 section 17.3). It describes the sandbox, not production.</summary>
/// <param name="Score">0 to 100, or null when no experiment ran.</param>
/// <param name="Capped">True when a data-loss invariant failure capped the score at 40.</param>
public sealed record ChaosScoreReport(
    int? Score,
    bool Capped,
    double HypothesisRate,
    double RecoveryScore,
    double DataLossPassRate,
    double AlertCorrectness,
    double RunbookSuccess,
    IReadOnlyList<string> Findings);

/// <summary>
/// The resilience score of section 17.3. The doc names the five inputs and the cap (one data-loss failure caps the score at 40);
/// the weights and the recovery formula are PROPOSED (17.5): weights 25, 25, 20, 15 and 15 percent, and a per-experiment recovery
/// score of min(1, target / recovery), zero when it never recovered.
/// </summary>
public static class ChaosResilienceScore
{
    public const int DataLossCap = 40;

    public static ChaosScoreReport Compute(IReadOnlyList<ChaosExperimentResult> results, TimeSpan recoveryTarget)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(recoveryTarget, TimeSpan.Zero);

        if (results.Count == 0)
        {
            return new ChaosScoreReport(null, false, 0, 0, 0, 0, 0, []);
        }

        double hypothesis = results.Count(r => r.HypothesisHeld) / (double)results.Count;
        double recovery = results.Average(r => r.Recovery is not { } time
            ? 0
            : time <= recoveryTarget ? 1 : recoveryTarget / time);
        double dataLoss = results.Count(r => r.DataLossInvariantHeld) / (double)results.Count;
        double alerts = results.Count(r => r.ExpectedAlertFired && !r.UnexpectedCriticalAlert) / (double)results.Count;
        List<ChaosExperimentResult> withRunbook = results.Where(r => r.RunbookSucceeded.HasValue).ToList();
        double runbooks = withRunbook.Count == 0 ? 1 : withRunbook.Count(r => r.RunbookSucceeded == true) / (double)withRunbook.Count;

        int score = (int)Math.Round(100 * ((0.25 * hypothesis) + (0.25 * recovery) + (0.20 * dataLoss) + (0.15 * alerts) + (0.15 * runbooks)));

        List<string> findings = [];
        foreach (ChaosExperimentResult failed in results.Where(r => !r.DataLossInvariantHeld))
        {
            findings.Add($"Data-loss invariant failed in experiment {failed.Experiment.Sequence} ({failed.Experiment.Kind}).");
        }

        foreach (ChaosExperimentResult stuck in results.Where(r => r.Outcome == ChaosOutcome.AbortTimedOut))
        {
            findings.Add($"Experiment {stuck.Experiment.Sequence} ({stuck.Experiment.Kind}) did not stop within the abort deadline.");
        }

        bool dataLost = results.Any(r => !r.DataLossInvariantHeld);
        bool capped = dataLost && score > DataLossCap;
        if (dataLost)
        {
            score = Math.Min(score, DataLossCap);
        }

        return new ChaosScoreReport(score, capped, hypothesis, recovery, dataLoss, alerts, runbooks, findings);
    }
}
