namespace Bazaar.Testing.LiveEval;

/// <summary>
/// Live-eval settings (doc 34, <c>test.eval.*</c>). Shipped defaults are the safe ones; an operator overrides them
/// in configuration. These are development spend for the test suite, not a gateway budget.
/// </summary>
public sealed record LiveEvalOptions
{
    /// <summary><c>test.eval.seed_count</c>: attempts per case per target. A case passes only if every attempt passes.</summary>
    public int SeedCount { get; init; } = 3;

    /// <summary><c>test.eval.holdout_count</c>: tasks held out from tuning.</summary>
    public int HoldoutCount { get; init; } = 10;

    /// <summary><c>test.eval.nightly_models</c>: models the nightly job runs.</summary>
    public IReadOnlyList<string> NightlyModels { get; init; } = ["claude-haiku-5-5"];

    /// <summary><c>test.eval.sonnet_on_demand</c>: whether Sonnet runs are started by hand.</summary>
    public bool SonnetOnDemand { get; init; } = true;

    /// <summary>
    /// <c>test.eval.judge_model</c>: unset by default. Judge scores stay hidden until calibrated, and the judge is a
    /// non-Anthropic model chosen at setup.
    /// </summary>
    public string? JudgeModel { get; init; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> or <see cref="ArgumentException"/> for an unusable value.</summary>
    public void Validate()
    {
        if (SeedCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SeedCount), SeedCount, "Seed count must be at least 1.");
        }

        if (HoldoutCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HoldoutCount), HoldoutCount, "Holdout count cannot be negative.");
        }

        if (NightlyModels.Count == 0 || NightlyModels.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Nightly models must be a non-empty list of model ids.", nameof(NightlyModels));
        }
    }
}
