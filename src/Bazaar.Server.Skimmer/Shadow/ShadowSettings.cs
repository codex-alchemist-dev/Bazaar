using Bazaar.Server.Skimmer.Rules;

namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>The settings the shadow gate reads (doc 13, "Settings"). Defaults match the settings table.</summary>
/// <param name="Mode"><c>skimmer.mode</c>.</param>
/// <param name="ThresholdBasisPoints"><c>skimmer.threshold_bp</c>, 0 to 10000. A score at or above it would be promoted.</param>
/// <param name="ScorerTimeout"><c>skimmer.scorer_timeout_ms</c>.</param>
/// <param name="Rules">The rules-stage settings.</param>
public sealed record ShadowSettings(
    SkimmerMode Mode = SkimmerMode.Shadow,
    int ThresholdBasisPoints = 7000,
    TimeSpan? ScorerTimeout = null,
    GateRuleSettings? Rules = null)
{
    /// <summary>The scorer timeout, 5000 ms by default.</summary>
    public TimeSpan EffectiveScorerTimeout => ScorerTimeout ?? TimeSpan.FromMilliseconds(5000);

    /// <summary>The rules-stage settings, defaults when none were given.</summary>
    public GateRuleSettings EffectiveRules => Rules ?? new GateRuleSettings();
}
