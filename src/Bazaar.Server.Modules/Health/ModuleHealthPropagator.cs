using Bazaar.Core;

namespace Bazaar.Server.Modules.Health;

/// <summary>
/// Spreads module health along the dependency graph (docs/design/46 section 10.1, docs/design/BAZAAR-COMPONENTS.md section 4.1).
/// A module that hard-depends on a Failing or Unknown module cannot be healthier than it: Failing blocks it (red) and Unknown leaves
/// it Unknown. A module that only soft-depends on a module that is not Healthy is at most Degraded (amber). A module's own worse
/// status always stands. Pure: the caller supplies manifests and probe reports.
/// </summary>
public static class ModuleHealthPropagator
{
    /// <summary>
    /// Returns one rollup per report, with dependency effects applied. A module with no report counts as Unknown (nothing was observed).
    /// Modules named in a report but absent from the manifests keep their own status.
    /// </summary>
    /// <exception cref="Bazaar.Core.ComponentGraphException">The manifests do not form a valid graph.</exception>
    public static IReadOnlyList<ModuleHealthRollup> Propagate(IEnumerable<ComponentManifest> manifests, IReadOnlyList<ModuleHealthReport> reports)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(reports);

        var own = new Dictionary<string, ModuleHealthStatus>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            ArgumentNullException.ThrowIfNull(report);
            ArgumentNullException.ThrowIfNull(report.Checks);
            if (!own.TryAdd(report.ModuleId, WorstOf(report.Checks)))
            {
                throw new ArgumentException($"Module '{report.ModuleId}' appears more than once.", nameof(reports));
            }
        }

        // Dependency order, so every hard target is settled before its dependents. Soft edges never reorder (section 4.1), so a
        // soft target may still be unsettled; its own status is the one that counts, which keeps mutual soft links stable.
        var effective = new Dictionary<string, ModuleHealthStatus>(StringComparer.Ordinal);
        var byId = ComponentGraph.Order(manifests).ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var manifest in byId.Values)
        {
            var status = own.GetValueOrDefault(manifest.Id, ModuleHealthStatus.Unknown);

            foreach (var target in manifest.DependsOn)
            {
                var targetStatus = effective.GetValueOrDefault(target, ModuleHealthStatus.Unknown);
                if (targetStatus is ModuleHealthStatus.Failing or ModuleHealthStatus.Unknown)
                {
                    status = Worse(status, targetStatus);
                }
                else if (targetStatus == ModuleHealthStatus.Degraded)
                {
                    status = Worse(status, ModuleHealthStatus.Degraded);
                }
            }

            foreach (var target in manifest.Soft)
            {
                var targetStatus = own.GetValueOrDefault(target, ModuleHealthStatus.Unknown);
                if (targetStatus != ModuleHealthStatus.Healthy)
                {
                    status = Worse(status, ModuleHealthStatus.Degraded);
                }
            }

            effective[manifest.Id] = status;
        }

        return reports
            .Select(r =>
            {
                var status = effective.GetValueOrDefault(r.ModuleId, own[r.ModuleId]);
                return new ModuleHealthRollup(r.ModuleId, status, ModuleHealthAggregator.LightOf(status));
            })
            .ToList();
    }

    private static ModuleHealthStatus WorstOf(IReadOnlyList<ModuleHealthCheck> checks) =>
        checks.Count == 0 ? ModuleHealthStatus.Unknown : checks.Select(c => c.Status).Aggregate(Worse);

    private static ModuleHealthStatus Worse(ModuleHealthStatus a, ModuleHealthStatus b) => Severity(b) > Severity(a) ? b : a;

    private static int Severity(ModuleHealthStatus status) => status switch
    {
        ModuleHealthStatus.Healthy => 0,
        ModuleHealthStatus.Degraded => 1,
        ModuleHealthStatus.Unknown => 2,
        _ => 3,
    };
}
