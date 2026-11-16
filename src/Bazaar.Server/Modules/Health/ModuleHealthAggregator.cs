namespace Bazaar.Server.Modules.Health;

/// <summary>The green, amber and red reading the Monitor pane shows (doc 46 section 12.1). Derived from status, never from the score.</summary>
public enum ModuleHealthLight
{
    Green,
    Amber,
    Red,
}

/// <summary>All probe results for one module.</summary>
/// <param name="ModuleId">The module's id.</param>
/// <param name="Checks">Its probe results (liveness, readiness, dependencies, self-test, or any others).</param>
public sealed record ModuleHealthReport(string ModuleId, IReadOnlyList<ModuleHealthCheck> Checks);

/// <summary>The worst status of one module and its light.</summary>
public sealed record ModuleHealthRollup(string ModuleId, ModuleHealthStatus Status, ModuleHealthLight Light);

/// <summary>The installation-wide reading: worst status, light, 0 to 100 score and the per-module rollups.</summary>
public sealed record ModuleHealthAggregate(
    ModuleHealthStatus Status,
    ModuleHealthLight Light,
    int Score,
    IReadOnlyList<ModuleHealthRollup> Modules);

/// <summary>
/// Aggregates module probe results. Pure: the caller supplies every result and the time. Overall status is the worst
/// of all parts. Severity order is Failing, then Unknown, then Degraded, then Healthy. Unknown is never green.
/// Score (implementation choice, doc 46 section 12.2 defines the caps only): the share of healthy checks, 0 to 100,
/// rounded down, capped at 49 when any check is Failing and at 70 when any check is Unknown. No checks gives an
/// Unknown aggregate with score 0.
/// </summary>
public static class ModuleHealthAggregator
{
    /// <summary>Score cap when any check is Failing (doc 46 section 12.2, failing critical probe).</summary>
    public const int FailingScoreCap = 49;

    /// <summary>Score cap when any check is Unknown (doc 46 section 12.2, unknown critical probe).</summary>
    public const int UnknownScoreCap = 70;

    public static ModuleHealthAggregate Aggregate(IReadOnlyList<ModuleHealthReport> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rollups = new List<ModuleHealthRollup>(modules.Count);
        var allChecks = new List<ModuleHealthCheck>();

        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            if (string.IsNullOrWhiteSpace(module.ModuleId))
            {
                throw new ArgumentException("Module id must not be blank.", nameof(modules));
            }

            if (!seen.Add(module.ModuleId))
            {
                throw new ArgumentException($"Module '{module.ModuleId}' appears more than once.", nameof(modules));
            }

            ArgumentNullException.ThrowIfNull(module.Checks);
            var status = WorstOf(module.Checks);
            rollups.Add(new ModuleHealthRollup(module.ModuleId, status, LightOf(status)));
            allChecks.AddRange(module.Checks);
        }

        var overall = WorstOf(allChecks);
        return new ModuleHealthAggregate(overall, LightOf(overall), ScoreOf(allChecks), rollups);
    }

    /// <summary>Maps a status to its light. Healthy is green, Failing is red, Degraded and Unknown are amber.</summary>
    public static ModuleHealthLight LightOf(ModuleHealthStatus status) => status switch
    {
        ModuleHealthStatus.Healthy => ModuleHealthLight.Green,
        ModuleHealthStatus.Failing => ModuleHealthLight.Red,
        _ => ModuleHealthLight.Amber,
    };

    // Worst status of a list. An empty list is Unknown: nothing was observed, so nothing is green.
    private static ModuleHealthStatus WorstOf(IReadOnlyList<ModuleHealthCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count == 0)
        {
            return ModuleHealthStatus.Unknown;
        }

        var worst = ModuleHealthStatus.Healthy;
        foreach (var check in checks)
        {
            ArgumentNullException.ThrowIfNull(check);
            if (Severity(check.Status) > Severity(worst))
            {
                worst = check.Status;
            }
        }

        return worst;
    }

    private static int Severity(ModuleHealthStatus status) => status switch
    {
        ModuleHealthStatus.Healthy => 0,
        ModuleHealthStatus.Degraded => 1,
        ModuleHealthStatus.Unknown => 2,
        _ => 3,
    };

    private static int ScoreOf(List<ModuleHealthCheck> checks)
    {
        if (checks.Count == 0)
        {
            return 0;
        }

        var healthy = checks.Count(c => c.Status == ModuleHealthStatus.Healthy);
        var score = healthy * 100 / checks.Count;

        if (checks.Any(c => c.Status == ModuleHealthStatus.Failing))
        {
            score = Math.Min(score, FailingScoreCap);
        }

        if (checks.Any(c => c.Status == ModuleHealthStatus.Unknown))
        {
            score = Math.Min(score, UnknownScoreCap);
        }

        return score;
    }
}
