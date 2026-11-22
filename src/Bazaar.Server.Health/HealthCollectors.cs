using System.Text.Json;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health;

/// <summary>A reverted merge, as the forge adapter reports it (doc 21).</summary>
public sealed record RevertFact(string Sha, DateTimeOffset At);

/// <summary>A card moved back out of Done (doc 12), from a Kanban or <c>gc_event</c> record.</summary>
public sealed record ReopenFact(string CardId, DateTimeOffset At);

/// <summary>A Council vote that was decided the other way on a re-vote (doc 10), from a <c>gc_event</c> record.</summary>
public sealed record VoteReversalFact(string VoteId, DateTimeOffset At);

/// <summary>
/// Turns debt and morale inputs into metric samples (doc 25 WP1). Every method is pure: no storage, no clock. A source with nothing
/// to say returns no sample (null or an empty list), because a missing input is a gap, never a zero.
/// </summary>
public static class HealthCollectors
{
    /// <summary>
    /// Counts warning-level results in a SARIF log (what the .NET analyzers emit). The toolchain is the dimension. Returns null when the
    /// text is not a SARIF log, so the caller records a gap instead of a zero.
    /// </summary>
    public static MetricSample? AnalyzerWarnings(string projectId, string toolchain, DateOnly day, string sarif, string sourceRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchain);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);

        try
        {
            using var doc = JsonDocument.Parse(sarif);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("runs", out var runs)
                || runs.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var warnings = 0;
            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object || !run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var result in results.EnumerateArray())
                {
                    if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("level", out var level) && level.GetString() == "warning")
                        warnings++;
                }
            }

            return new MetricSample(projectId, HealthMetricNames.AnalyzerWarnings, toolchain, day, warnings, sourceRef);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Counts distinct vulnerable packages (id and version) in <c>dotnet list package --vulnerable --format json</c> output, top-level and
    /// transitive. Returns null when the text is not that report.
    /// </summary>
    public static MetricSample? PackageVulnerabilities(string projectId, DateOnly day, string auditJson, string sourceRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);

        try
        {
            using var doc = JsonDocument.Parse(auditJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var vulnerable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in projects.EnumerateArray())
            {
                if (project.ValueKind != JsonValueKind.Object || !project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var framework in frameworks.EnumerateArray())
                {
                    foreach (var list in new[] { "topLevelPackages", "transitivePackages" })
                    {
                        if (framework.ValueKind != JsonValueKind.Object || !framework.TryGetProperty(list, out var packages) || packages.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var pkg in packages.EnumerateArray())
                        {
                            if (pkg.ValueKind == JsonValueKind.Object && pkg.TryGetProperty("vulnerabilities", out var v)
                                && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                            {
                                var id = pkg.TryGetProperty("id", out var i) ? i.GetString() : null;
                                var version = pkg.TryGetProperty("resolvedVersion", out var r) ? r.GetString() : null;
                                vulnerable.Add($"{id}@{version}");
                            }
                        }
                    }
                }
            }

            return new MetricSample(projectId, HealthMetricNames.PackageVulnerabilities, string.Empty, day, vulnerable.Count, sourceRef);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One <c>ci.reverts</c> sample per UTC day with a revert. A repeated sha counts once. Days without a revert produce nothing.</summary>
    public static IReadOnlyList<MetricSample> Reverts(string projectId, IEnumerable<RevertFact> reverts) =>
        CountPerDay(projectId, HealthMetricNames.Reverts, "revert", reverts.Select(r => (r.Sha, r.At)));

    /// <summary>One <c>kanban.reopened_tasks</c> sample per UTC day. A repeated card and day counts once.</summary>
    public static IReadOnlyList<MetricSample> ReopenedTasks(string projectId, IEnumerable<ReopenFact> reopens) =>
        CountPerDay(projectId, HealthMetricNames.ReopenedTasks, "reopen", reopens.Select(r => (r.CardId, r.At)));

    /// <summary>One <c>gc.vote_reversals</c> sample per UTC day. A repeated vote id counts once.</summary>
    public static IReadOnlyList<MetricSample> VoteReversals(string projectId, IEnumerable<VoteReversalFact> reversals) =>
        CountPerDay(projectId, HealthMetricNames.VoteReversals, "reversal", reversals.Select(r => (r.VoteId, r.At)));

    /// <summary>
    /// Blocked share of the cards on the board that day (doc 12). Returns null for an empty board: a share of nothing is a gap, not zero.
    /// </summary>
    public static MetricSample? BlockedShare(string projectId, DateOnly day, int blockedCards, int totalCards)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentOutOfRangeException.ThrowIfNegative(blockedCards);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockedCards, totalCards);

        return totalCards == 0
            ? null
            : new MetricSample(projectId, HealthMetricNames.BlockedShare, string.Empty, day, Math.Round((double)blockedCards / totalCards, 4), $"kanban:{day:yyyy-MM-dd}");
    }

    private static List<MetricSample> CountPerDay(string projectId, string metric, string prefix, IEnumerable<(string Id, DateTimeOffset At)> facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(facts);

        return facts
            .Select(f => (f.Id, Day: DateOnly.FromDateTime(f.At.UtcDateTime)))
            .Distinct()
            .GroupBy(f => f.Day)
            .OrderBy(g => g.Key)
            .Select(g => new MetricSample(projectId, metric, string.Empty, g.Key, g.Count(), $"{prefix}:{g.Key:yyyy-MM-dd}"))
            .ToList();
    }
}
