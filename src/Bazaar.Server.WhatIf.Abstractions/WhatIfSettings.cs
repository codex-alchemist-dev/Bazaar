namespace Bazaar.Server.WhatIf;

/// <summary>
/// Operator settings for the What If module. <c>what_if.reputation_weight</c> is a decimal from 0 to 1, default 0 (doc 26 Settings):
/// What If notes weigh 0 in the reputation ledger unless the operator raises it.
/// </summary>
public sealed record WhatIfSettings
{
    public const string ReputationWeightKey = "what_if.reputation_weight";

    public WhatIfSettings(decimal reputationWeight = 0)
    {
        Validate(reputationWeight);
        ReputationWeight = reputationWeight;
    }

    public decimal ReputationWeight { get; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when the weight is outside 0 to 1.</summary>
    public static void Validate(decimal reputationWeight)
    {
        if (reputationWeight is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(reputationWeight), reputationWeight, $"{ReputationWeightKey} must be between 0 and 1.");
    }
}
