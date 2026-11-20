namespace Bazaar.Server.Knowledge.Health;

/// <summary>
/// Shipped defaults for the health thresholds (doc 25 settings table). Shared so the Server settings registry
/// (<c>health.coverage_drop_points</c>, <c>health.build_failure_streak</c>) and the health rules read one value.
/// </summary>
public static class HealthDefaults
{
    /// <summary>Coverage drop, in percentage points, that raises an alert.</summary>
    public const int CoverageDropPoints = 2;

    /// <summary>Consecutive failing CI days that raise an alert.</summary>
    public const int BuildFailureStreak = 3;
}
