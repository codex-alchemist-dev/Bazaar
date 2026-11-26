namespace Bazaar.Kodo.Slo;

/// <summary>How serious a burn alert is. Maps to the alert severities of the Responder; a warning burn also feeds the evaluator's SLO warning flag.</summary>
public enum SloBurnSeverity
{
    /// <summary>Burning faster than the budget allows.</summary>
    Warning,

    /// <summary>Burning fast enough to need action now.</summary>
    Critical,
}

/// <summary>
/// One multi-window burn rule (doc 46 section 12.3). It fires only when the burn rate is at or above the threshold over both the short
/// and the long window: the short window makes it quick, the long one keeps a brief spike from alerting.
/// </summary>
/// <param name="Name">The rule name, an identifier.</param>
/// <param name="Severity">The alert severity.</param>
/// <param name="ShortWindow">The short window.</param>
/// <param name="LongWindow">The long window, longer than the short one.</param>
/// <param name="ThresholdMilli">The burn rate that fires it in thousandths (6000 is 6 times the sustainable rate; 1000 is exactly on budget).</param>
public sealed record SloBurnRule(string Name, SloBurnSeverity Severity, TimeSpan ShortWindow, TimeSpan LongWindow, int ThresholdMilli);

/// <summary>
/// The set of burn rules. The defaults use the two windows of section 12.3 (1 hour and 6 hours). A setting may only tighten them: the
/// same rule names, no higher threshold, no longer window, never below the floors. Nothing here is DECIDED.
/// </summary>
public sealed class SloBurnPolicy
{
    /// <summary>No threshold may be below 1 times the sustainable rate: a burn under 1 is inside the budget.</summary>
    public const int MinThresholdMilli = 1_000;

    /// <summary>No short window may be below one minute.</summary>
    public static readonly TimeSpan MinShortWindow = TimeSpan.FromMinutes(1);

    private SloBurnPolicy(IReadOnlyList<SloBurnRule> rules) => Rules = rules;

    /// <summary>The documented defaults: a warning at 2 times and a critical at 6 times, both over 1 hour and 6 hours.</summary>
    public static SloBurnPolicy Default { get; } = new(
    [
        new SloBurnRule("fast_burn", SloBurnSeverity.Critical, TimeSpan.FromHours(1), TimeSpan.FromHours(6), 6_000),
        new SloBurnRule("slow_burn", SloBurnSeverity.Warning, TimeSpan.FromHours(1), TimeSpan.FromHours(6), 2_000),
    ]);

    /// <summary>The rules, in a fixed order.</summary>
    public IReadOnlyList<SloBurnRule> Rules { get; }

    /// <summary>The longest long window of any rule.</summary>
    public TimeSpan LongestWindow => Rules.Max(r => r.LongWindow);

    /// <summary>
    /// Builds a policy from requested rules. Returns false, and no policy, unless the requested rules are exactly the default rule names
    /// (any order), each with the default severity, a threshold from the floor up to the default, a short window from the floor up to the
    /// default, and a long window above the short one and no longer than the default.
    /// </summary>
    public static bool TryTighten(IReadOnlyList<SloBurnRule> requested, out SloBurnPolicy? policy)
    {
        policy = null;
        if (requested is null || requested.Count != Default.Rules.Count)
        {
            return false;
        }

        List<SloBurnRule> result = [];
        foreach (SloBurnRule baseline in Default.Rules)
        {
            SloBurnRule[] matches = [.. requested.Where(r => r is not null && string.Equals(r.Name, baseline.Name, StringComparison.Ordinal))];
            if (matches.Length != 1)
            {
                return false;
            }

            SloBurnRule rule = matches[0];
            if (rule.Severity != baseline.Severity
                || rule.ThresholdMilli < MinThresholdMilli || rule.ThresholdMilli > baseline.ThresholdMilli
                || rule.ShortWindow < MinShortWindow || rule.ShortWindow > baseline.ShortWindow
                || rule.LongWindow <= rule.ShortWindow || rule.LongWindow > baseline.LongWindow)
            {
                return false;
            }

            result.Add(rule);
        }

        policy = new SloBurnPolicy(result);
        return true;
    }
}
