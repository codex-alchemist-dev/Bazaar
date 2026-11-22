using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Slo;

/// <summary>An SLO's standing. The order is least to most serious, with <see cref="NoData"/> first because it never reads as healthy.</summary>
public enum SloHealth
{
    /// <summary>No sample in the window. Shown as unknown and reported with no budget left; never as healthy.</summary>
    NoData,

    /// <summary>Inside budget and no burn rule firing.</summary>
    Ok,

    /// <summary>At least one burn rule is firing, none critical, and the budget is not used up.</summary>
    Warning,

    /// <summary>A critical burn rule is firing, or the budget is used up.</summary>
    Breached,
}

/// <summary>One burn rule's reading for one SLO. A burn figure is null when its window holds no sample, and a rule never fires on a null.</summary>
/// <param name="RuleName">The rule name.</param>
/// <param name="Severity">The rule severity.</param>
/// <param name="ShortBurnMilli">The burn rate over the short window in thousandths of the sustainable rate, or null.</param>
/// <param name="LongBurnMilli">The burn rate over the long window, or null.</param>
/// <param name="Firing">Both burns are at or above the rule threshold.</param>
public sealed record SloBurnReading(string RuleName, SloBurnSeverity Severity, int? ShortBurnMilli, int? LongBurnMilli, bool Firing);

/// <summary>
/// One SLO's computed status (doc 46 section 12.3). Identifiers and numbers only, so it is safe to journal, report and show.
/// Units are slots for the availability kind and samples for the latency kind. Bad and unobserved units both consume budget.
/// </summary>
/// <param name="Id">The SLO id.</param>
/// <param name="Kind">The indicator kind.</param>
/// <param name="TargetPpm">The target in parts per million.</param>
/// <param name="Window">The rolling window.</param>
/// <param name="EvaluatedAt">The instant given to the evaluator, in UTC.</param>
/// <param name="Health">The standing.</param>
/// <param name="GoodUnits">Good units in the window.</param>
/// <param name="BadUnits">Bad units.</param>
/// <param name="UnobservedUnits">Unobserved units, including slots with no sample after the first one recorded (availability only).</param>
/// <param name="AchievedPpm">Good units over observed-or-not units in parts per million (rounded down), or null with no data.</param>
/// <param name="ConsumedBudgetPpm">The share of the error budget used, in parts per million, not capped (above 1000000 means overspent).</param>
/// <param name="RemainingBudgetPpm">The share left, from 0 to 1000000. Zero with no data.</param>
/// <param name="BurnRules">Each rule's reading, in policy order.</param>
public sealed record SloStatus(
    string Id,
    SloIndicatorKind Kind,
    int TargetPpm,
    TimeSpan Window,
    DateTimeOffset EvaluatedAt,
    SloHealth Health,
    long GoodUnits,
    long BadUnits,
    long UnobservedUnits,
    int? AchievedPpm,
    long ConsumedBudgetPpm,
    int RemainingBudgetPpm,
    IReadOnlyList<SloBurnReading> BurnRules)
{
    /// <summary>All units counted in the window.</summary>
    public long TotalUnits => GoodUnits + BadUnits + UnobservedUnits;

    /// <summary>The burn rules that are firing.</summary>
    public IEnumerable<SloBurnReading> FiringRules => BurnRules.Where(r => r.Firing);
}

/// <summary>All SLO statuses at one instant, sorted by id.</summary>
/// <param name="EvaluatedAt">The instant, in UTC.</param>
/// <param name="Statuses">The statuses, ordinal by id.</param>
public sealed record SloSnapshot(DateTimeOffset EvaluatedAt, IReadOnlyList<SloStatus> Statuses)
{
    /// <summary>Any burn rule is firing (the evaluator's <c>SloWarningBurn</c>, section 8.1).</summary>
    public bool WarningBurn => Statuses.Any(s => s.BurnRules.Any(r => r.Firing));

    /// <summary>Any SLO is breached (the evaluator's <c>SloBreach</c>, section 8.2).</summary>
    public bool Breach => Statuses.Any(s => s.Health == SloHealth.Breached);

    /// <summary>Any SLO has no data. This is shown, and does not change the state by itself.</summary>
    public bool AnyNoData => Statuses.Any(s => s.Health == SloHealth.NoData);

    /// <summary>Returns the observation with the SLO flags raised. A flag already raised stays raised; this never lowers one.</summary>
    public KodoObservation ApplyTo(KodoObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation with
        {
            SloWarningBurn = observation.SloWarningBurn || WarningBurn,
            SloBreach = observation.SloBreach || Breach,
        };
    }
}
