namespace Bazaar.Kodo.Evaluator;

/// <summary>
/// The Kodo categories that feed the 0 to 100 health score (doc 46 sections 9.1 and 12.2).
/// </summary>
public enum KodoCategory
{
    Server,
    Data,
    Security,
    Modules,
    Host,
    Network,
    Fleet,
    Models,
}

/// <summary>
/// The 0 to 100 health score (doc 46 section 12.2). Pure: the same inputs always give the same score.
/// </summary>
public static class KodoScore
{
    /// <summary>Cap when any critical probe is red (section 12.2: "a failing critical probe caps the overall score at 49").</summary>
    public const int CriticalRedCap = 49;

    /// <summary>Cap when an integrity failure is open (section 12.2: "an integrity failure caps it at 10").</summary>
    public const int IntegrityFailureCap = 10;

    /// <summary>Cap when a critical probe is <c>unknown</c> (section 12.2: "an unknown critical probe caps it at 70").</summary>
    public const int CriticalUnknownCap = 70;

    /// <summary>The category weights of section 12.2: server 25, data 20, security 15, modules 15, host 10, network 5, fleet 5, models 5.</summary>
    public static IReadOnlyDictionary<KodoCategory, int> DefaultWeights { get; } = new Dictionary<KodoCategory, int>
    {
        [KodoCategory.Server] = 25,
        [KodoCategory.Data] = 20,
        [KodoCategory.Security] = 15,
        [KodoCategory.Modules] = 15,
        [KodoCategory.Host] = 10,
        [KodoCategory.Network] = 5,
        [KodoCategory.Fleet] = 5,
        [KodoCategory.Models] = 5,
    }.AsReadOnly();

    /// <summary>
    /// Computes the overall score: the weighted mean of the category scores, rounded half up, then capped.
    /// The caps are a safety floor, so the lowest applicable cap wins.
    /// </summary>
    /// <param name="categoryScores">A score from 0 to 100 for every category.</param>
    /// <param name="observation">The same observation the state was evaluated from. Only its critical facts are read.</param>
    /// <param name="weights">Category weights, all non-negative with a positive total. Defaults to <see cref="DefaultWeights"/>.</param>
    public static int Compute(
        IReadOnlyDictionary<KodoCategory, int> categoryScores,
        KodoObservation observation,
        IReadOnlyDictionary<KodoCategory, int>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(categoryScores);
        ArgumentNullException.ThrowIfNull(observation);
        weights ??= DefaultWeights;

        long weighted = 0;
        long totalWeight = 0;
        foreach (KodoCategory category in Enum.GetValues<KodoCategory>())
        {
            if (!categoryScores.TryGetValue(category, out int score))
            {
                throw new ArgumentException($"No score was given for category {category}.", nameof(categoryScores));
            }

            if (score is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(categoryScores), score, $"The score for {category} must be from 0 to 100.");
            }

            if (!weights.TryGetValue(category, out int weight) || weight < 0)
            {
                throw new ArgumentException($"The weight for {category} must be present and non-negative.", nameof(weights));
            }

            weighted += (long)weight * score;
            totalWeight += weight;
        }

        if (totalWeight == 0)
        {
            throw new ArgumentException("The category weights must have a positive total.", nameof(weights));
        }

        // Integer division with a half-up rounding offset: (2 * weighted + total) / (2 * total).
        int raw = (int)((2 * weighted + totalWeight) / (2 * totalWeight));

        int cap = 100;
        if (observation.CriticalRed)
        {
            cap = Math.Min(cap, CriticalRedCap);
        }

        if (observation.IntegrityOrContractFailure)
        {
            cap = Math.Min(cap, IntegrityFailureCap);
        }

        if (observation.CriticalUnknown)
        {
            cap = Math.Min(cap, CriticalUnknownCap);
        }

        return Math.Min(raw, cap);
    }
}
